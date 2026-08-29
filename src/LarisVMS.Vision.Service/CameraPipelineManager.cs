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
            _logger.LogInformation("Replacing existing pipeline for camera {CameraId} with updated configuration.", request.CameraId);
            await existing.DisposeAsync();
        }

        var family = Enum.Parse<DetectionModelFamily>(request.ModelFamily);
        var dfineWeights = Enum.Parse<DFineWeights>(request.DFineWeights);
        var aspectMode = Enum.Parse<AspectMode>(request.AspectMode);
        var modelKey = $"{family}|{dfineWeights}";
        var resolvedModelPath = _resolvedModelPathsByKey.GetOrAdd(modelKey,
            _ => ResolveModelPath(_options.ModelPath, family, dfineWeights, _logger));

        var http = _httpClientFactory.CreateClient(nameof(CameraDetectionPipeline));
        var pipeline = new CameraDetectionPipeline(request, _options, _ffmpegPath, resolvedModelPath, family, dfineWeights, aspectMode, http, _loggerFactory);
        _pipelines[request.CameraId] = pipeline;
        _logger.LogInformation("Started watching camera {CameraId} ({Width}x{Height}, hwaccel: {Hwaccel}).",
            request.CameraId, request.Width, request.Height, request.HardwareAcceleration ?? "none");
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
    internal static string ResolveModelPath(string configuredPath, DetectionModelFamily family, DFineWeights dfineWeights, ILogger logger)
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

        var wantedFileName = DetectionModelCatalog.GetFileName(family, dfineWeights);
        var wantedPath = Path.Combine(directory, wantedFileName);
        if (File.Exists(wantedPath))
        {
            logger.LogInformation("Using detection model {ModelPath} for {Family}/{Weights}.", wantedPath, family, dfineWeights);
            return wantedPath;
        }

        var candidates = Directory.GetFiles(directory, "*.onnx");
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

        await pipeline.DisposeAsync();
        _logger.LogInformation("Stopped watching camera {CameraId}.", cameraId);
        return true;
    }

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
