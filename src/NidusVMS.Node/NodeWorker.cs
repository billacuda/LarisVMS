using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NidusVMS.Core.Dtos;
using NidusVMS.Media;

namespace NidusVMS.Node;

public class NodeWorker(NodeApiClient api, string ffmpegPath, string fallbackStorageRoot, int livePort,
    string? initialMediaSigningKey, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger<NodeWorker> _logger = loggerFactory.CreateLogger<NodeWorker>();

    // Concurrent, not a plain Dictionary: written only from the reconcile loop, but read from
    // Program.cs's /live WebSocket endpoint (M5) on ASP.NET Core's own request threads — a live
    // viewer looking up a camera's active RecordingSession is a genuine concurrent reader.
    private readonly ConcurrentDictionary<Guid, CameraRecorder> _active = new();
    private readonly ConcurrentQueue<SegmentReportItem> _pendingSegments = new();
    private readonly ConcurrentQueue<StreamInfoReportItem> _pendingStreamInfo = new();

    /// <summary>The active RecordingSession for a camera this node is currently recording, or null
    /// if it isn't assigned here (or isn't recording yet). Used by the live-view WebSocket endpoint
    /// to attach a viewer to the right session's tee'd live fanout.</summary>
    public RecordingSession? TryGetSession(Guid cameraId) => _active.TryGetValue(cameraId, out var r) ? r.Session : null;

    /// <summary>The key currently used to validate incoming live-view tokens. Seeded from the locally
    /// persisted registration (set for any node that registered after M5 shipped), then kept current
    /// by every reconcile cycle's GetConfigAsync response — the server hands this out through the
    /// same config the node already polls every 30s, specifically so a node that registered *before*
    /// M5 (and so has none stored locally) self-heals within one cycle without needing to
    /// re-register, which would mean a brand new NodeId. Volatile: read from the live endpoint's
    /// request threads, written from the reconcile loop.</summary>
    public volatile string? MediaSigningKey = initialMediaSigningKey;

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
                MediaSigningKey = config.MediaSigningKey;
                var storageRoot = Reconcile(config, ct);
                var usage = DiskSpace.TryGetUsage(storageRoot);
                await api.HeartbeatAsync(new NodeHeartbeatRequest(NodeVersion.Current, usage?.FreeBytes, usage?.TotalBytes, livePort), ct);
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
            if (_active.TryRemove(cameraId, out var recorder)) recorder.Cts.Cancel();
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
