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

        var http = _httpClientFactory.CreateClient(nameof(CameraDetectionPipeline));
        var pipeline = new CameraDetectionPipeline(request, _options, _ffmpegPath, http, _loggerFactory);
        _pipelines[request.CameraId] = pipeline;
        _logger.LogInformation("Started watching camera {CameraId} ({Width}x{Height}, hwaccel: {Hwaccel}).",
            request.CameraId, request.Width, request.Height, request.HardwareAcceleration ?? "none");
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
