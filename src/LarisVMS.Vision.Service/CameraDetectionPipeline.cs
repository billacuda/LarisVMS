using System.Collections.Concurrent;
using System.Net.Http.Json;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Media;
using LarisVMS.Vision.Capture;
using LarisVMS.Vision.Detection;
using LarisVMS.Vision.Inference;
using LarisVMS.Vision.Tracking;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Service;

/// <summary>
/// Owns one camera's whole detection pipeline: capture (VisionSession, decision 1) → inference
/// (YoloEngine) → tracking (ByteTracker) → movement classification + best-frame tracking
/// (MovementClassifier, decisions 7/10) → per-label debouncing (MotionHysteresis, the same class
/// DahuaCgiEventSession already uses, decision 7's own "same shape" call-out) → reporting back to
/// Node (decision 3).
///
/// One YoloEngine per camera, not shared across cameras watched by the same process: YoloDotNet's
/// Yolo holds pinned buffers reused across calls and is explicitly not thread-safe (see YoloEngine's
/// own doc comment) — aitest's own CameraPipeline already established "one loaded model instance
/// per camera pipeline" as the correct ownership model, even though every camera loads the same
/// underlying .onnx file independently.
///
/// Per-label rather than per-track hysteresis/reporting: multiple simultaneous instances of the
/// same class (two cars in frame at once) collapse into one reported span for that label, the same
/// accepted simplification DahuaCgiEventSession's own Dictionary&lt;DetectionKind, MotionHysteresis&gt;
/// already makes. Best-frame tracking follows the same per-label grain — MovementClassifier itself
/// tracks best-frame per *track*, so this class additionally tracks the best-scoring frame across
/// every track sharing a label, using the identical scoring formula (MovementClassifier.Score), so
/// a label's reported span always cites whichever specific sighting was clearest across every
/// instance of it, not just whichever track happened to be observed last.
/// </summary>
public sealed class CameraDetectionPipeline : IAsyncDisposable
{
    // Same cadence NodeWorker.EnqueueMotionCheckpoints already uses for the equivalent purpose.
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckpointRecency = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReportFlushInterval = TimeSpan.FromSeconds(2);

    private readonly VisionStartCameraRequest _request;
    private readonly VisionSession _session;
    private readonly LatestFrameSlot _slot;
    private readonly YoloEngine _engine;
    private readonly ByteTracker _tracker = new();
    private readonly MovementClassifier _movement = new();
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runTask;

    // Per-label state — see this class's own doc comment for why the grain is the label, not the
    // track. Only ever touched from the single inference loop below, so plain (not concurrent)
    // collections are safe.
    private readonly Dictionary<string, MotionHysteresis> _hysteresisByLabel = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (BestFrame Frame, double Score)> _bestFrameByLabel = new(StringComparer.OrdinalIgnoreCase);

    // Read by the control API's GET /cameras/{id}/detections handler on a request thread, written
    // by the inference loop — a plain volatile reference swap (never mutated in place) is enough:
    // a reader either sees the previous frame's snapshot or the new one, never a half-built list.
    private volatile IReadOnlyList<VisionLiveDetectionBox> _liveSnapshot = [];

    // Enqueued by the inference loop the instant a span closes, drained by its own loop below —
    // decoupled from the inference loop the same way NodeWorker's own SegmentReportLoopAsync is
    // decoupled from RecordingSession's frame-reading, so a slow/unreachable Node callback can
    // never back-pressure detection itself.
    private readonly ConcurrentQueue<VisionDetectionReportItem> _pendingReports = new();

    public CameraDetectionPipeline(VisionStartCameraRequest request, VisionServiceOptions serviceOptions,
        string resolvedFfmpegPath, string resolvedModelPath, HttpClient http, ILoggerFactory loggerFactory)
    {
        _request = request;
        _http = http;
        _logger = loggerFactory.CreateLogger($"Vision[{request.CameraId}]");

        _slot = new LatestFrameSlot(request.Width, request.Height);
        _session = new VisionSession(
            new VisionSessionOptions(resolvedFfmpegPath, request.RtspUri, request.Width, request.Height, request.HardwareAcceleration),
            _slot, loggerFactory.CreateLogger<VisionSession>());

        _engine = new YoloEngine(new EngineOptions
        {
            ModelPath = resolvedModelPath,
            GpuId = serviceOptions.GpuId,
            CudnnPath = serviceOptions.CudnnPath,
            EnableTensorRt = serviceOptions.EnableTensorRt,
            TensorRtPrecision = serviceOptions.TensorRtPrecision,
            TensorRtEngineCachePath = serviceOptions.TensorRtEngineCachePath,
            TensorRtLibPath = serviceOptions.TensorRtLibPath,
            OpenVinoDeviceType = serviceOptions.OpenVinoDeviceType,
        }, loggerFactory.CreateLogger<YoloEngine>());

        _runTask = RunAsync(_cts.Token);
    }

    public IReadOnlyList<VisionLiveDetectionBox> GetLiveSnapshot() => _liveSnapshot;

    private async Task RunAsync(CancellationToken ct)
    {
        var sessionTask = _session.RunAsync(ct);
        var inferenceTask = InferenceLoopAsync(ct);
        var checkpointTask = CheckpointLoopAsync(ct);
        var reportFlushTask = ReportFlushLoopAsync(ct);

        try
        {
            await Task.WhenAll(sessionTask, inferenceTask, checkpointTask, reportFlushTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera detection pipeline for {CameraId} failed.", _request.CameraId);
        }
    }

    private async Task InferenceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var frame = await _slot.TakeAsync(ct);
            if (frame is null) break; // cancelled or disposed

            List<YoloDotNet.Models.ObjectDetection> detections;
            try
            {
                detections = _engine.Detect(frame, _request.Confidence, _request.Iou);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inference failed on one frame — skipping it.");
                continue;
            }

            var tracked = _tracker.Update(detections);
            var now = DateTime.UtcNow;
            // IDetection.Id is nullable at the interface level (an untracked detection has none),
            // but every detection ByteTracker.Update actually returns has already had a real track
            // id written into it (see ByteTracker.Update's own final step) — the null-filter here is
            // defensive, not an expected case.
            _movement.Prune(tracked.Where(d => d.Id.HasValue).Select(d => d.Id!.Value).ToHashSet());

            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liveBoxes = new List<VisionLiveDetectionBox>(tracked.Count);

            foreach (var detection in tracked)
            {
                if (detection.Id is not { } trackId) continue;

                var label = detection.Label?.Name ?? "object";
                var category = CocoCategoryMap.Resolve(label);
                seenLabels.Add(label);

                var observation = _movement.Observe(trackId, detection.BoundingBox, detection.Confidence,
                    frame.Width, frame.Height, now);

                UpdateBestFrameForLabel(label, detection, frame.Width, frame.Height, now);

                // Idle tracks never open/extend a span unless ReportIdleDetections is on — decision
                // 7's "not interested in static objects" default. A Moving track always does.
                var motionPresent = observation.State == MovementState.Moving || _request.ReportIdleDetections;
                var hysteresis = GetOrCreateHysteresis(label);
                if (hysteresis.Observe(now, motionPresent, detection.Confidence) is { } closed)
                {
                    EnqueueReport(closed, label, category);
                }

                liveBoxes.Add(new VisionLiveDetectionBox(
                    trackId, category, label, observation.State.ToString(),
                    detection.BoundingBox.Left / (double)frame.Width,
                    detection.BoundingBox.Top / (double)frame.Height,
                    detection.BoundingBox.Width / (double)frame.Width,
                    detection.BoundingBox.Height / (double)frame.Height,
                    detection.Confidence));
            }

            // A label with no instance in this frame still needs to observe absence, so its
            // hysteresis can wind down and eventually close — mirroring MotionSession's own
            // per-zone loop, which evaluates every zone every frame regardless of current activity.
            foreach (var (label, hysteresis) in _hysteresisByLabel)
            {
                if (seenLabels.Contains(label)) continue;
                if (hysteresis.Observe(now, motionPresent: false, score: 0) is { } closed)
                {
                    EnqueueReport(closed, label, CocoCategoryMap.Resolve(label));
                }
            }

            _liveSnapshot = liveBoxes;
        }
    }

    private void UpdateBestFrameForLabel(string label, YoloDotNet.Models.ObjectDetection detection, int frameWidth, int frameHeight, DateTime nowUtc)
    {
        if (frameWidth <= 0 || frameHeight <= 0) return;

        var box = detection.BoundingBox;
        var x = box.Left / (double)frameWidth;
        var y = box.Top / (double)frameHeight;
        var w = box.Width / (double)frameWidth;
        var h = box.Height / (double)frameHeight;
        var normalizedArea = Math.Clamp(w * h, 0.0, 1.0);
        var score = MovementClassifier.Score(detection.Confidence, normalizedArea);

        if (!_bestFrameByLabel.TryGetValue(label, out var current) || score > current.Score)
        {
            _bestFrameByLabel[label] = (new BestFrame(nowUtc, x, y, w, h, detection.Confidence), score);
        }
    }

    private MotionHysteresis GetOrCreateHysteresis(string label)
    {
        if (!_hysteresisByLabel.TryGetValue(label, out var hysteresis))
        {
            // Zero debounce on both edges, the same choice DahuaCgiEventSession makes: ByteTrack's
            // own two-stage association plus its "confirmed only after a second corroborating
            // frame" activation rule already provide the "is this real" gating a startAfter/endAfter
            // debounce exists for elsewhere (raw per-frame pixel motion has no such confirmation of
            // its own).
            hysteresis = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.Zero);
            _hysteresisByLabel[label] = hysteresis;
        }
        return hysteresis;
    }

    private void EnqueueReport(MotionSpanResult span, string label, string category)
    {
        var best = _bestFrameByLabel.TryGetValue(label, out var b) ? b.Frame : (BestFrame?)null;
        _pendingReports.Enqueue(new VisionDetectionReportItem(
            _request.CameraId, span.StartUtc, span.EndUtc, span.PeakScore,
            category, label,
            best?.AtUtc, best?.X, best?.Y, best?.W, best?.H, best?.Confidence));
    }

    private async Task CheckpointLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(CheckpointInterval, ct); }
            catch (OperationCanceledException) { break; }

            var now = DateTime.UtcNow;
            foreach (var (label, hysteresis) in _hysteresisByLabel)
            {
                if (hysteresis.CurrentInProgressSpan(now, CheckpointRecency) is { } span)
                {
                    EnqueueReport(span, label, CocoCategoryMap.Resolve(label));
                }
            }
        }
    }

    private async Task ReportFlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(ReportFlushInterval, ct); }
            catch (OperationCanceledException) { break; }

            await FlushPendingReportsAsync(CancellationToken.None);
        }
    }

    private async Task FlushPendingReportsAsync(CancellationToken ct)
    {
        var batch = new List<VisionDetectionReportItem>();
        while (_pendingReports.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count == 0) return;

        var callbackUrl = $"{_request.NodeCallbackBaseUrl.TrimEnd('/')}/detections";
        foreach (var item in batch)
        {
            try
            {
                var response = await _http.PostAsJsonAsync(callbackUrl, item, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to report a detection span for camera {CameraId} to Node — will retry.", _request.CameraId);
                _pendingReports.Enqueue(item);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _runTask; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Pipeline shutdown task ended with an error (harmless — already stopping).");
        }

        // Close out whatever was still in progress rather than silently dropping it — same
        // shutdown philosophy MotionSession.RunAsync's own FlushAll uses.
        var now = DateTime.UtcNow;
        foreach (var (label, hysteresis) in _hysteresisByLabel)
        {
            if (hysteresis.Flush(now) is { } closed) EnqueueReport(closed, label, CocoCategoryMap.Resolve(label));
        }
        await FlushPendingReportsAsync(CancellationToken.None);

        _cts.Dispose();
        _slot.Dispose();
        _engine.Dispose();
    }
}
