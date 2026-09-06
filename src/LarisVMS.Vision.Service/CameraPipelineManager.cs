using System.Collections.Concurrent;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Media;
using LarisVMS.Vision.Inference;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LarisVMS.Vision.Service;

/// <summary>
/// Owns every camera this process is currently watching. Purely reactive to Node's own explicit
/// start/stop control-API calls (decision 3) — unlike NodeWorker's own reconcile loops, this has no
/// periodic loop of its own deciding what *should* be running; Node is the single source of truth
/// for that (NodeWorker.ReconcileVision, comparing a per-camera configuration signature the same
/// way ReconcileMotion/ReconcileEvents already do), so a start call here always just replaces
/// whatever was running for that camera, no signature comparison needed on this side too.
/// </summary>
public sealed class CameraPipelineManager : IAsyncDisposable
{
    private readonly VisionServiceOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CameraPipelineManager> _logger;
    private readonly string _ffmpegPath;

    private readonly ConcurrentDictionary<Guid, CameraDetectionPipeline> _pipelines = new();

    // Keyed by "{family}|{dfineWeights}" and resolved lazily on first use per key, same reasoning as
    // the single-model _resolvedModelPath field this replaced: resolution throws when there's no
    // model to load, and DI resolves this singleton *before* the /start handler body runs — an
    // exception here would escape that handler's own try/catch and come back as a bare 500 with
    // none of the detail that catch exists to report. A dictionary rather than one cached string
    // because a node's own DetectionModelFamily setting can change between reconciles (a new
    // camera's /start request could arrive with a different resolved family than an already-running
    // one was started with).
    private readonly ConcurrentDictionary<string, string> _resolvedModelPathsByKey = new();

    // One gate per model key so a slow first-use YOLOX model fetch (Vision Service -> Node -> server)
    // doesn't run twice when two cameras' /start calls race for the same size.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelResolveGates = new();

    public CameraPipelineManager(IOptions<VisionServiceOptions> options, IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory, ILogger<CameraPipelineManager> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _ffmpegPath = FfmpegPathResolver.Resolve(_options.FfmpegPath);
        _logger.LogInformation("Resolved ffmpeg path: {FfmpegPath}", _ffmpegPath);
    }

    public async Task StartOrReplaceAsync(VisionStartCameraRequest request)
    {
        if (_pipelines.TryRemove(request.CameraId, out var existing))
        {
            _logger.LogInformation("Replacing existing pipeline for camera {Camera} with updated configuration.", request.DisplayName);
            await existing.DisposeAsync();
        }

        var family = Enum.Parse<DetectionModelFamily>(request.ModelFamily);
        var dfineWeights = Enum.Parse<DFineWeights>(request.DFineWeights);
        var yoloXSize = Enum.Parse<YoloXSize>(request.YoloXSize);
        var aspectMode = Enum.Parse<AspectMode>(request.AspectMode);

        // Detection.DFineTensorRtMode resolved for this camera: the value the server pushed on the
        // request wins; blank (an older node build) falls back to this machine's own local
        // Vision:DFineTensorRtMode. Only ever acted on for a D-FINE pipeline.
        var dfineTensorRtMode = string.IsNullOrWhiteSpace(request.DFineTensorRtMode)
            ? (_options.DFineTensorRtMode ?? "Off")
            : request.DFineTensorRtMode;

        // FP16 for D-FINE needs a mixed-precision *.fp16.onnx model (backbone FP16, decoder FP32) —
        // a straight FP16 cast overflows D-FINE's transformer decoder. No conversion toolchain
        // produces a correct one yet, so FP16 is bundled-model-gated: it activates automatically if
        // a *.fp16.onnx is present in the models directory, and otherwise transparently runs FP32
        // TensorRT with a warning. Everything downstream (catalog filename, engine cache key, the
        // trt_layer_norm_fp32_fallback belt) is already wired for when that model exists.
        var dfineFp16Mixed = false;
        if (family == DetectionModelFamily.DFine && _options.EnableTensorRt
            && dfineTensorRtMode.Trim().Equals("FP16", StringComparison.OrdinalIgnoreCase))
        {
            var mixedName = DetectionModelCatalog.GetFileName(family, dfineWeights, yoloXSize, dfineFp16Mixed: true);
            dfineFp16Mixed = File.Exists(Path.Combine(ModelsDirectory(_options.ModelPath), mixedName));
            if (!dfineFp16Mixed)
            {
                _logger.LogWarning(
                    "Detection.DFineTensorRtMode is FP16 for camera {Camera} but no mixed-precision model " +
                    "({MixedName}) is bundled — running D-FINE on FP32 TensorRT instead. FP16 for D-FINE is not " +
                    "yet available (a straight FP16 cast overflows its transformer decoder).",
                    request.DisplayName, mixedName);
                dfineTensorRtMode = "FP32";
            }
        }

        var modelKey = $"{family}|{dfineWeights}|{yoloXSize}|{(dfineFp16Mixed ? "fp16" : "std")}";
        var http = _httpClientFactory.CreateClient(nameof(CameraDetectionPipeline));
        var resolvedModelPath = await ResolveModelPathCachedAsync(modelKey, family, dfineWeights, yoloXSize, dfineFp16Mixed, request.NodeCallbackBaseUrl, http);

        var pipeline = new CameraDetectionPipeline(request, _options, _ffmpegPath, resolvedModelPath, family, dfineWeights, yoloXSize, aspectMode, dfineTensorRtMode, http, _loggerFactory);
        _pipelines[request.CameraId] = pipeline;
        _logger.LogInformation("Started watching camera {Camera} (id {CameraId}, {Width}x{Height}, hwaccel: {Hwaccel}).",
            request.DisplayName, request.CameraId, request.Width, request.Height, request.HardwareAcceleration ?? "none");
    }

    /// <summary>
    /// Picks the .onnx file to load for a resolved (family, weights) selection. <see
    /// cref="VisionServiceOptions.ModelPath"/>'s default names a specific file
    /// (<c>models/model.onnx</c>), but nothing in this project ever produces that name — so a
    /// configured path that exists still wins outright (an operator naming a specific model keeps
    /// getting exactly that one), but otherwise this looks for the exact filename
    /// DetectionModelCatalog expects for the resolved selection
    /// (tools/export-models/fetch_dfine.py's own output), falling back to an alphabetical glob only
    /// as a last resort for a stale/hand-placed file — the tier that let YOLOv9 keep silently
    /// running after this integration replaced it, back when the glob was the *only* lookup.
    /// </summary>
    private async Task<string> ResolveModelPathCachedAsync(string modelKey, DetectionModelFamily family,
        DFineWeights dfineWeights, YoloXSize yoloXSize, bool dfineFp16Mixed, string nodeCallbackBaseUrl, HttpClient http)
    {
        if (_resolvedModelPathsByKey.TryGetValue(modelKey, out var cached)) return cached;

        var gate = _modelResolveGates.GetOrAdd(modelKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (_resolvedModelPathsByKey.TryGetValue(modelKey, out cached)) return cached;

            var resolved = family == DetectionModelFamily.YoloX
                ? await ResolveYoloXModelPathAsync(yoloXSize, nodeCallbackBaseUrl, http)
                : ResolveModelPath(_options.ModelPath, family, dfineWeights, yoloXSize, _logger, dfineFp16Mixed);

            _resolvedModelPathsByKey[modelKey] = resolved;
            return resolved;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string ModelsDirectory(string configuredPath)
    {
        var configured = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
        return Path.GetDirectoryName(configured)
            ?? throw new InvalidOperationException($"Configured model path '{configuredPath}' has no directory.");
    }

    /// <summary>YOLOX models aren't bundled (unlike D-FINE) — one is fetched from the server via the
    /// Node's own loopback proxy on first use and cached in the models directory alongside the
    /// bundled D-FINE files. A fetch failure throws so the /start handler reports it (the camera
    /// stays unwatched) rather than the pipeline crashing later on a missing file.</summary>
    private async Task<string> ResolveYoloXModelPathAsync(YoloXSize size, string nodeCallbackBaseUrl, HttpClient http)
    {
        var dir = ModelsDirectory(_options.ModelPath);
        Directory.CreateDirectory(dir);
        var fileName = DetectionModelCatalog.GetYoloXFileName(size);
        var path = Path.Combine(dir, fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            _logger.LogInformation("Using cached YOLOX model {ModelPath}.", path);
            return path;
        }

        var url = $"{nodeCallbackBaseUrl.TrimEnd('/')}/internal/detection-model/yolox/{size.ToString().ToLowerInvariant()}";
        _logger.LogInformation("YOLOX-{Size} model not cached — fetching it from the server via {Url}.", size, url);

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not obtain the YOLOX-{size} model from the server (via the node's model proxy at {url}). " +
                "The server needs one-time outbound access to the pinned model host, or the model seeded into its " +
                "detection-models cache directory.", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new InvalidOperationException(
                $"The node's model proxy returned {(int)response.StatusCode} for YOLOX-{size} at {url}. " +
                "Seed the server's detection-models cache or check its outbound access to the pinned model host.");
        }

        var tmp = path + ".tmp";
        try
        {
            await using (var src = await response.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);

            if (new FileInfo(tmp).Length == 0) throw new InvalidOperationException("downloaded an empty file");
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            response.Dispose();
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best effort */ } }
        }

        _logger.LogInformation("Fetched and cached YOLOX-{Size} model to {ModelPath} ({Bytes} bytes).",
            size, path, new FileInfo(path).Length);
        return path;
    }

    internal static string ResolveModelPath(string configuredPath, DetectionModelFamily family, DFineWeights dfineWeights,
        YoloXSize yoloXSize, ILogger logger, bool dfineFp16Mixed = false)
    {
        // Relative to the app's own directory, not the current working directory: this is a fact
        // about where the package's files live, and the process is launched by NodeWorker's
        // supervisor rather than from a shell whose cwd means anything.
        string ToAbsolute(string path) =>
            Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

        var configured = ToAbsolute(configuredPath);
        if (File.Exists(configured)) return configured;

        var directory = Path.GetDirectoryName(configured);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new FileNotFoundException(
                $"No detection model directory at '{directory ?? configuredPath}'. Fetch a model with " +
                "tools/export-models/fetch_dfine.py and rebuild the node package with build-node.ps1 so it " +
                "gets bundled.");
        }

        var wantedFileName = DetectionModelCatalog.GetFileName(family, dfineWeights, yoloXSize, dfineFp16Mixed);
        var wantedPath = Path.Combine(directory, wantedFileName);
        if (File.Exists(wantedPath))
        {
            logger.LogInformation("Using detection model {ModelPath} for {Family}/{Weights}.", wantedPath, family, dfineWeights);
            return wantedPath;
        }

        // FP16 D-FINE needs its own mixed-precision export — the plain FP32 file run under
        // trt_fp16_enable is the silent-NaN case. The caller (CameraPipelineManager) only sets
        // dfineFp16Mixed after confirming the file exists, so reaching here means it vanished between
        // that check and this resolve: a hard stop, never a fall-through to the glob below.
        if (dfineFp16Mixed)
        {
            throw new FileNotFoundException(
                $"Detection.DFineTensorRtMode is FP16 but the mixed-precision model '{wantedFileName}' is not in " +
                $"'{directory}'. Set Detection.DFineTensorRtMode to FP32 or Off, or restore the model file.");
        }

        // The *.fp16.onnx mixed-precision exports are a distinct artifact class tied to a specific
        // TensorRT mode, never a stand-in for a missing plain model — exclude them from the
        // last-resort glob so an FP32/Off run can't silently land on one.
        var candidates = Directory.GetFiles(directory, "*.onnx")
            .Where(f => !f.EndsWith(".fp16.onnx", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
        if (candidates.Length == 0)
        {
            throw new FileNotFoundException(
                $"No detection model found in '{directory}' — expected '{wantedFileName}' for the configured " +
                $"{family}/{dfineWeights} selection. Fetch it with tools/export-models/fetch_dfine.py and " +
                "rebuild the node package with build-node.ps1 so it gets bundled.");
        }

        var chosen = candidates[0];
        logger.LogWarning(
            "Expected model '{Wanted}' for the configured {Family}/{Weights} selection was not found in " +
            "'{Directory}' — falling back to the alphabetically-first bundled .onnx ({Chosen}) out of " +
            "{Count} found ({Models}). This is likely a stale or incomplete package — re-run fetch_dfine.py " +
            "and rebuild.", wantedFileName, family, dfineWeights, directory, Path.GetFileName(chosen),
            candidates.Length, string.Join(", ", candidates.Select(Path.GetFileName)));
        return chosen;
    }

    public async Task<bool> StopAsync(Guid cameraId)
    {
        if (!_pipelines.TryRemove(cameraId, out var pipeline)) return false;

        var name = pipeline.DisplayName;
        await pipeline.DisposeAsync();
        _logger.LogInformation("Stopped watching camera {Camera}.", name);
        return true;
    }

    /// <summary>The cameras this process is actually watching, for NodeWorker's reconcile to compare
    /// its own view against. Two ways the two can drift, both silent before this existed: the Vision
    /// Service restarted within a single reconcile tick (EnsureRunning brings it back before
    /// ReconcileVision looks at IsRunning, so Node never re-sends its starts), and — now that the
    /// engine is built after /start has returned — a pipeline whose engine build failed, which is
    /// running but will never infer. Both show up here as an absent camera id.</summary>
    public IReadOnlyList<Guid> WatchedCameraIds() =>
        [.. _pipelines.Where(p => !p.Value.EngineBuildFailed).Select(p => p.Key)];

    /// <summary>Null when this camera isn't currently being watched — the caller (Node's own
    /// /live/{cameraId}/detections WS relay) treats that the same as "no boxes right now" rather
    /// than an error.</summary>
    public VisionLiveDetectionsResponse? GetLiveDetections(Guid cameraId)
    {
        if (!_pipelines.TryGetValue(cameraId, out var pipeline)) return null;
        return new VisionLiveDetectionsResponse(cameraId, DateTime.UtcNow, [.. pipeline.GetLiveSnapshot()]);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pipeline in _pipelines.Values) await pipeline.DisposeAsync();
        _pipelines.Clear();
    }
}
