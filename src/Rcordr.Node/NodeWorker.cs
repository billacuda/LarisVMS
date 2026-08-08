using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rcordr.Core.Dtos;
using Rcordr.Media;

namespace Rcordr.Node;

public class NodeWorker(NodeApiClient api, string ffmpegPath, string fallbackStorageRoot, ILoggerFactory loggerFactory)
    : BackgroundService
{
    private readonly ILogger<NodeWorker> _logger = loggerFactory.CreateLogger<NodeWorker>();
    private readonly Dictionary<Guid, CameraRecorder> _active = [];
    private readonly ConcurrentQueue<SegmentReportItem> _pendingSegments = new();
    private readonly ConcurrentQueue<StreamInfoReportItem> _pendingStreamInfo = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconcileLoop = ReconcileLoopAsync(stoppingToken);
        var segmentReportLoop = SegmentReportLoopAsync(stoppingToken);

        try
        {
            await Task.WhenAll(reconcileLoop, segmentReportLoop);
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var recorder in _active.Values) recorder.Cts.Cancel();
            await Task.WhenAll(_active.Values.Select(r => r.RunTask));
            await FlushSegmentsAsync(CancellationToken.None);
            await FlushStreamInfoAsync(CancellationToken.None);
        }
    }

    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var config = await api.GetConfigAsync(ct);
                var storageRoot = Reconcile(config, ct);
                var usage = DiskSpace.TryGetUsage(storageRoot);
                await api.HeartbeatAsync(new NodeHeartbeatRequest(NodeVersion.Current, usage?.FreeBytes, usage?.TotalBytes), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Heartbeat/config cycle failed — will retry.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SegmentReportLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }

            await FlushSegmentsAsync(ct);
            await FlushStreamInfoAsync(ct);
        }
    }

    private async Task FlushSegmentsAsync(CancellationToken ct)
    {
        var batch = new List<SegmentReportItem>();
        while (_pendingSegments.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count == 0) return;

        try
        {
            await api.ReportSegmentsAsync(batch, ct);
            _logger.LogInformation("Reported {Count} segment(s).", batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Re-queue on failure (server unreachable) rather than lose the record of what was
            // actually written to disk — the segment file already exists regardless of whether the
            // control plane knows about it yet.
            foreach (var item in batch) _pendingSegments.Enqueue(item);
            _logger.LogWarning(ex, "Failed to report {Count} segment(s) — will retry next cycle.", batch.Count);
        }
    }

    private async Task FlushStreamInfoAsync(CancellationToken ct)
    {
        var batch = new List<StreamInfoReportItem>();
        while (_pendingStreamInfo.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count == 0) return;

        try
        {
            await api.ReportStreamInfoAsync(batch, ct);
            _logger.LogInformation("Reported stream info for {Count} stream(s).", batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same re-queue-on-failure reasoning as segments — ffmpeg only prints this once per
            // (re)start, so losing it here means waiting for the next restart rather than the next
            // report cycle.
            foreach (var item in batch) _pendingStreamInfo.Enqueue(item);
            _logger.LogWarning(ex, "Failed to report stream info for {Count} stream(s) — will retry next cycle.", batch.Count);
        }
    }

    private string Reconcile(NodeConfigResponse config, CancellationToken stoppingToken)
    {
        var storageRoot = string.IsNullOrWhiteSpace(config.StorageRootPath) ? fallbackStorageRoot : config.StorageRootPath;
        var desired = config.Cameras.ToDictionary(c => c.CameraId);

        foreach (var cameraId in _active.Keys.Except(desired.Keys).ToList())
        {
            _logger.LogInformation("Camera {CameraId} no longer assigned to this node — stopping.", cameraId);
            _active[cameraId].Cts.Cancel();
            _active.Remove(cameraId);
        }

        foreach (var camera in config.Cameras)
        {
            if (_active.ContainsKey(camera.CameraId)) continue;

            // Recording only ever uses the Main stream — Sub/Third exist for the live wall and
            // motion detection (M5/M8), not for what gets written to disk, per the plan's
            // "Main / sub stream" design.
            var mainStream = camera.Streams.FirstOrDefault(s => s.Role == "Main");
            if (mainStream is null)
            {
                _logger.LogWarning("Camera {CameraId} ({Name}) has no Main stream — skipping.", camera.CameraId, camera.Name);
                continue;
            }

            var rtspUri = InjectCredentials(mainStream.RtspUri, camera.Username, camera.Password);
            var outputDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "main");

            var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var sessionLogger = loggerFactory.CreateLogger($"Recording[{camera.Name}]");
            var session = new RecordingSession(new RecordingSessionOptions(ffmpegPath, rtspUri, outputDir), sessionLogger);
            session.SegmentCompleted += segment => _pendingSegments.Enqueue(new SegmentReportItem(
                camera.CameraId, "Main", segment.StartUtc, segment.EndUtc, segment.FilePath, segment.SizeBytes,
                mainStream.Codec, mainStream.Width, mainStream.Height, mainStream.HasAudio));
            session.StreamResolutionDetected += resolution => _pendingStreamInfo.Enqueue(new StreamInfoReportItem(
                camera.CameraId, "Main", resolution.Width, resolution.Height, resolution.Codec));

            var runTask = session.RunAsync(cts.Token);
            _active[camera.CameraId] = new CameraRecorder(cts, runTask, session);
            _logger.LogInformation("Started recording camera {CameraId} ({Name}) -> {OutputDir}", camera.CameraId, camera.Name, outputDir);
        }

        return storageRoot;
    }

    private static string InjectCredentials(string rtspUri, string? username, string? password)
    {
        if (string.IsNullOrEmpty(username)) return rtspUri;

        var uri = new Uri(rtspUri);
        var userInfo = string.IsNullOrEmpty(password)
            ? Uri.EscapeDataString(username)
            : $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";

        // Built manually rather than via UriBuilder.UserName/Password, which apply their own
        // escaping on top of values already escaped above (double-encoding).
        return $"{uri.Scheme}://{userInfo}@{uri.Authority}{uri.PathAndQuery}";
    }
}
