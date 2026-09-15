using System.Collections.Concurrent;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Media;
using LarisVMS.Vision.Inference;
using LarisVMS.Vision.Models;
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
    /// <summary>Named <see cref="HttpClient"/> for Detection.Backend = "ExternalHttp" traffic only —
    /// registered separately from <c>nameof(CameraDetectionPipeline)</c> (which still carries the
    /// Node callbacks, POST /detections and /detections/crop) so a slow or overloaded external
    /// inference service's connections don't compete with report traffic that has nothing to do
    /// with it. See this constant's registration in Program.cs for the connection-pool settings.</summary>
    public const string ExternalInferenceHttpClientName = "ExternalInferenceDetect";

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

        // FP16 for D-FINE now means "run the plain FP32 .onnx under trt_fp16_enable=1 +
        // trt_layer_norm_fp32_fallback=1" (the TensorRT-level mitigation) — not a separate
        // mixed-precision *.fp16.onnx export. That mixed-precision file mechanism
        // (DetectionModelCatalog's dfineFp16Mixed parameter) remains in the code but is no longer
        // triggered from here: no working conversion toolchain produces a correct one, and the
        // TensorRT-level path plus CameraDetectionPipeline's automatic overflow-to-FP32 rebuild
        // (DFineEngine.HasNonFiniteOverflow) supersede it. See the D-FINE FP16 section of the
        // LarisVMS+SideGlance planning notes.
        const bool dfineFp16Mixed = false;

        var modelKey = $"{family}|{dfineWeights}|{yoloXSize}|{(dfineFp16Mixed ? "fp16" : "std")}";
        var http = _httpClientFactory.CreateClient(nameof(CameraDetectionPipeline));

        // Detection.Backend = "ExternalHttp": no local .onnx is loaded — the pipeline builds an
        // HttpDetectionEngine from the request's ExternalInference* fields instead — so there is no
        // model path to resolve (and no accelerator needed; this backend runs on a GPU-less node).
        var external = string.Equals(request.DetectionBackend, "ExternalHttp", StringComparison.OrdinalIgnoreCase);

        // DetectionModelFamily.Custom: the model comes from Detection.LocalModelName + ModelDiscovery
        // instead of the hardcoded family/weights/size catalog — resolved separately below rather than
        // through ResolveModelPathCachedAsync, since a Custom model's path and its resolved decoder
        // both come from the same scan and must travel together.
        DiscoveredModel? customModel = null;
        string resolvedModelPath;
        if (external)
        {
            resolvedModelPath = string.Empty;
        }
        else if (family == DetectionModelFamily.Custom)
        {
            customModel = ResolveCustomModel(request.LocalModelName);
            if (customModel is null)
            {
                throw new InvalidOperationException(
                    $"Detection.ModelFamily is \"Custom\" for camera {request.DisplayName} but " +
                    $"Detection.LocalModelName (\"{request.LocalModelName}\") does not name a usable model in " +
                    $"{ModelsDirectory(_options.ModelPath)} — check Admin > Nodes for a metadata/sidecar warning.");
            }
            resolvedModelPath = customModel.OnnxPath;
        }
        else
        {
            resolvedModelPath = await ResolveModelPathCachedAsync(modelKey, family, dfineWeights, yoloXSize, dfineFp16Mixed, request.NodeCallbackBaseUrl, http);
        }
        // Only resolved for the backend that actually needs it — see ExternalInferenceHttpClientName's
        // own doc comment for why this is a separate client from the Node-callback one above.
        var externalHttp = external ? _httpClientFactory.CreateClient(ExternalInferenceHttpClientName) : null;

        var pipeline = new CameraDetectionPipeline(request, _options, _ffmpegPath, resolvedModelPath, family, dfineWeights, yoloXSize, aspectMode, dfineTensorRtMode, http, _loggerFactory, externalHttp, customModel);
        _pipelines[request.CameraId] = pipeline;
        _logger.LogInformation("Started watching camera {Camera} (id {CameraId}, {Width}x{Height}, hwaccel: {Hwaccel}).",
            request.DisplayName, request.CameraId, request.Width, request.Height, request.HardwareAcceleration ?? "none");
    }

    /// <summary>Lists every model <see cref="ModelDiscovery"/> finds in this node's models directory,
    /// for the <c>GET /models</c> endpoint the Admin dropdown (via Node's <c>/vision/models</c> proxy)
    /// probes. Rescanned on every call rather than cached — this is an on-demand admin action (page
    /// load, "refresh models"), not a hot path, so a rescan's cost (reading every .onnx's header) is
    /// worth paying for always-current results (a model dropped in seconds ago shows up immediately).</summary>
    public IReadOnlyList<DiscoveredModel> ListDiscoveredModels() =>
        ModelDiscovery.Scan(ModelsDirectory(_options.ModelPath), _logger);

    /// <summary>Resolves Detection.LocalModelName against this node's models directory for a Custom-
    /// family pipeline. Null when the name is blank, not found, or found but unusable (no valid
    /// descriptor/decoder) — the caller (<see cref="StartOrReplaceAsync"/>) turns that into a clear
    /// startup failure rather than a confusing downstream one.</summary>
    private DiscoveredModel? ResolveCustomModel(string localModelName)
    {
        if (string.IsNullOrWhiteSpace(localModelName)) return null;

        var found = ModelDiscovery.Scan(ModelsDirectory(_options.ModelPath), _logger)
            .FirstOrDefault(m => string.Equals(m.Name, localModelName, StringComparison.OrdinalIgnoreCase));
        return found is { IsUsable: true } ? found : null;
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

    /// <summary>The default, always-exists-or-creatable location models live in —
    /// <c>C:\ProgramData\LarisVMS\models</c>, the same %ProgramData%\LarisVMS root the TensorRT cache
    /// and vision log already use (see <see cref="OrtSessionFactory.ResolveTensorRtCachePath"/>).
    /// Models are never bundled into the package here on — an operator drops <c>.onnx</c> files (and
    /// optional same-basename <c>.json</c> sidecars) into this folder directly; see
    /// <see cref="Models.ModelDiscovery"/>.</summary>
    public static string DefaultModelsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "models");

    private static string ModelsDirectory(string configuredPath)
    {
        // An unrooted configured path resolves against the shared ProgramData models directory, not
        // the app's own install directory — models are operator-managed data, not something the
        // package ships (see DefaultModelsDirectory's own doc comment). Only the file name of a
        // relative configured path is honored (the default "models/model.onnx" means "the standard
        // location", not a "models" subfolder underneath it — that whole folder now *is* "models").
        var configured = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(DefaultModelsDirectory, Path.GetFileName(configuredPath));
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
        // An unrooted configured path resolves against the shared ProgramData models directory (see
        // DefaultModelsDirectory's own doc comment) — models are never bundled into the package, so
        // there is nothing "relative to the app's own directory" to resolve against any more.
        string ToAbsolute(string path) =>
            Path.IsPathRooted(path) ? path : Path.Combine(DefaultModelsDirectory, Path.GetFileName(path));

        var configured = ToAbsolute(configuredPath);
        if (File.Exists(configured)) return configured;

        var directory = Path.GetDirectoryName(configured);
        if (directory is null)
        {
            throw new FileNotFoundException($"Configured model path '{configuredPath}' has no directory.");
        }
        Directory.CreateDirectory(directory); // first run on a fresh install — the operator drops files in after.

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
                $"{family}/{dfineWeights} selection. Drop the .onnx file into {directory} (models are no longer " +
                "bundled with the node package) — see tools/export-models for how to obtain one.");
        }

        var chosen = candidates[0];
        logger.LogWarning(
            "Expected model '{Wanted}' for the configured {Family}/{Weights} selection was not found in " +
            "'{Directory}' — falling back to the alphabetically-first .onnx found there ({Chosen}) out of " +
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

    /// <summary>Per-camera engine-build state for the Dashboard's "still starting AI detection"
    /// spinner — a separate, richer endpoint from <see cref="WatchedCameraIds"/> above, which must
    /// keep returning a bare id list for <c>NodeWorker.PruneStaleVisionWatchesAsync</c>.</summary>
    public IReadOnlyList<Core.Dtos.VisionCameraStatusDto> GetCameraStatuses() =>
        [.. _pipelines.Select(p => new Core.Dtos.VisionCameraStatusDto(p.Key, p.Value.IsEngineBuilding, p.Value.EngineBuildFailed))];

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
