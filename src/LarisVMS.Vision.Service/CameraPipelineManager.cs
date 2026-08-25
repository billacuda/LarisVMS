using System.Collections.Concurrent;
using LarisVMS.Core.Dtos;
using LarisVMS.Media;
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

    // Resolved on first use rather than in the constructor: this throws when there's no model to
    // load, and DI resolves this singleton *before* the /start handler body runs — an exception here
    // would escape that handler's own try/catch and come back as a bare 500 with none of the detail
    // that catch exists to report.
    private string? _resolvedModelPath;

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

        _resolvedModelPath ??= ResolveModelPath(_options.ModelPath, _logger);

        var http = _httpClientFactory.CreateClient(nameof(CameraDetectionPipeline));
        var pipeline = new CameraDetectionPipeline(request, _options, _ffmpegPath, _resolvedModelPath, http, _loggerFactory);
        _pipelines[request.CameraId] = pipeline;
        _logger.LogInformation("Started watching camera {CameraId} ({Width}x{Height}, hwaccel: {Hwaccel}).",
            request.CameraId, request.Width, request.Height, request.HardwareAcceleration ?? "none");
    }

    /// <summary>
    /// Picks the .onnx file to load. <see cref="VisionServiceOptions.ModelPath"/>'s default names a
    /// specific file (<c>models/model.onnx</c>), but nothing in this project ever produces that name:
    /// tools/export-models emits the upstream weight name (<c>yolo9-t.onnx</c>, <c>yolo9-s.onnx</c>,
    /// ...) and build-node.ps1 bundles whatever .onnx files exist verbatim, so the default could
    /// never match a real package and AI detection failed on every node with "File doesn't exist"
    /// regardless of how correctly everything else was set up.
    ///
    /// So a configured path that exists still wins outright — an operator naming a specific model
    /// keeps getting exactly that one — but otherwise this falls back to discovering what was
    /// actually bundled, which is what makes a default install work with no configuration at all.
    /// Multiple models is the *normal* case rather than an error (export.py's own default set is two
    /// of them), so it picks deterministically by name and says which, instead of refusing to start.
    /// </summary>
    internal static string ResolveModelPath(string configuredPath, ILogger logger)
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
                $"No detection model directory at '{directory ?? configuredPath}'. Export a model with " +
                "tools/export-models/ and rebuild the node package with build-node.ps1 so it gets bundled.");
        }

        var candidates = Directory.GetFiles(directory, "*.onnx");
        Array.Sort(candidates, StringComparer.OrdinalIgnoreCase);
        if (candidates.Length == 0)
        {
            throw new FileNotFoundException(
                $"No .onnx model found in '{directory}'. Export one with tools/export-models/ and rebuild " +
                "the node package with build-node.ps1 so it gets bundled.");
        }

        var chosen = candidates[0];
        if (candidates.Length > 1)
        {
            logger.LogWarning(
                "{Count} models are bundled ({Models}) — using {Chosen}. Set Vision:ModelPath to choose a " +
                "different one.", candidates.Length,
                string.Join(", ", candidates.Select(Path.GetFileName)), Path.GetFileName(chosen));
        }
        logger.LogInformation("Using detection model {ModelPath}.", chosen);
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
