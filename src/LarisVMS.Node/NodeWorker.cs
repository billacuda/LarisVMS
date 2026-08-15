using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Media;
using LarisVMS.Node.Update;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Node;

public class NodeWorker(NodeApiClient api, string ffmpegPath, string fallbackStorageRoot, int livePort,
    NodeConfig registration, ILoggerFactory loggerFactory, OnvifEventsClient onvifEventsClient,
    UpdateService updateService) : BackgroundService
{
    private readonly ILogger<NodeWorker> _logger = loggerFactory.CreateLogger<NodeWorker>();

    // Concurrent, not a plain Dictionary: written only from the reconcile loop, but read from
    // Program.cs's /live WebSocket endpoint (M5) on ASP.NET Core's own request threads — a live
    // viewer looking up a camera's active RecordingSession is a genuine concurrent reader.
    private readonly ConcurrentDictionary<Guid, CameraRecorder> _active = new();
    private readonly ConcurrentQueue<SegmentReportItem> _pendingSegments = new();
    private readonly ConcurrentQueue<StreamInfoReportItem> _pendingStreamInfo = new();

    // M8: only ever written from the reconcile loop, same as _active, though nothing on a request
    // thread reads it in pass 1 (no live motion-state endpoint yet) — kept concurrent anyway so
    // ExecuteAsync's shutdown path (ends both dictionaries the same way) doesn't need special-casing.
    private readonly ConcurrentDictionary<Guid, CameraMotionRecorder> _activeMotion = new();
    private readonly ConcurrentQueue<MotionSpanReportItem> _pendingMotionSpans = new();

    // M8 pass 6: same shape as _activeMotion/_pendingMotionSpans above, for ONVIF PullPoint camera
    // events instead of server-side substream frame-diffing — a camera can have either signal
    // source, both, or neither, independently (see ReconcileEvents and DecideMotionSegment's use of
    // both dictionaries together).
    private readonly ConcurrentDictionary<Guid, CameraEventRecorder> _activeEvents = new();
    private readonly ConcurrentQueue<CameraEventReportItem> _pendingCameraEvents = new();

    // M8 pass 2: each camera's SegmentCompleted handler (see HandleSegmentCompleted) fires from
    // that camera's own RecordingSession.RunAsync task, so multiple cameras can call in
    // concurrently — a plain HashSet would race. TryAdd used purely as a "have we warned about
    // this camera already" gate, value itself is unused.
    private readonly ConcurrentDictionary<Guid, byte> _warnedMotionModeMissingSession = new();

    // Schedule/Event pass: same TryAdd-gate shape as _warnedMotionModeMissingSession above, one per
    // new mode's own "nothing configured to gate on" case.
    private readonly ConcurrentDictionary<Guid, byte> _warnedEventModeMissingRule = new();
    private readonly ConcurrentDictionary<Guid, byte> _warnedScheduleModeMissingWindows = new();

    // M8 pass 3, generalized in the Schedule/Event pass: a Motion- or Event-mode segment's
    // keep/discard decision is deferred (not made the instant it completes) so an event starting
    // shortly *after* a segment ends can still claim it as pre-roll — see
    // PendingGatedSegmentDecision and GatedDecisionLoopAsync. Concurrent for the same reason as the
    // other queues here: enqueued from whichever camera's RecordingSession task happens to complete
    // a segment, dequeued from the single decision loop. Schedule mode never enqueues here — see
    // HandleSegmentCompleted's doc comment for why its decision needs no deferral.
    private readonly ConcurrentQueue<PendingGatedSegmentDecision> _pendingGatedDecisions = new();

    // M8 pass 6: a real, confirmed bug — RecordingSession.SegmentCompleted used to close over the
    // NodeConfigCameraDto from whichever reconcile cycle first started that camera's recording, and
    // was only ever wired up once (inside "if a camera isn't already active"). Editing Recording
    // Mode (or the pre/post-roll seconds) on a camera that was already recording under Continuous
    // therefore had no effect at all — not delayed, not partial, silently *nothing* — until the
    // session happened to restart for an unrelated reason (camera reassignment, node restart).
    // Confirmed live: three cameras set to Motion mode had every single segment retained with zero
    // gaps for 35+ hours, which is only possible if the gating code was never actually being
    // reached. Updated every reconcile for every camera (new or already active), read fresh by
    // HandleSegmentCompleted on every completed segment instead of trusting a value from whenever
    // the session happened to start.
    private readonly ConcurrentDictionary<Guid, NodeConfigCameraDto> _latestCameraConfig = new();

    // Only ever touched from ReconcileLoopAsync's single loop — never concurrently, unlike
    // MediaSigningKey below, so this doesn't need to be volatile. Seeded from whatever this node
    // had cached from its last successful reconcile before this process started; see
    // TryStartFromCacheIfIdle.
    private NodeConfigResponse? _cachedConfig = registration.CachedConfig;

    // Fetched once, before any RecordingSession can start (see ExecuteAsync) — a real, confirmed bug
    // fix: without this, every node process restart made RecordingSession.RunAsync rescan a camera's
    // *entire* on-disk history with zero memory of what the server already has rows for, re-firing
    // SegmentCompleted for all of it. For a Motion-mode camera, a freshly-restarted MotionSession/
    // CameraEventSession has observed no motion yet at that exact moment, so nearly all of that
    // re-fired history looked like "no motion" and was wrongly discarded — deleting files that
    // already had valid, previously-reported Segments rows, on every single restart. Confirmed live:
    // Segments dropped from ~16,600 to ~235 rows after this session's several version-bump restarts.
    private HashSet<string>? _knownSegmentPaths;

    /// <summary>The active RecordingSession for a camera this node is currently recording, or null
    /// if it isn't assigned here (or isn't recording yet). Used by the live-view WebSocket endpoint
    /// to attach a viewer to the right session's tee'd live fanout.</summary>
    public RecordingSession? TryGetSession(Guid cameraId) => _active.TryGetValue(cameraId, out var r) ? r.Session : null;

    /// <summary>Opens a short-lived RTSP session against the camera's Main stream and grabs one
    /// frame — null if the camera isn't assigned to this node (nothing to grab from) or the grab
    /// itself fails (camera unreachable, auth failure, timeout — see SnapshotCapture).</summary>
    public Task<byte[]?> CaptureSnapshotAsync(Guid cameraId, CancellationToken ct) =>
        _active.TryGetValue(cameraId, out var recorder)
            ? SnapshotCapture.CaptureAsync(ffmpegPath, recorder.RtspUri, ct)
            : Task.FromResult<byte[]?>(null);

    /// <summary>M7 pass 2: extracts one JPEG frame from an already-recorded segment file — unlike
    /// CaptureSnapshotAsync (live RTSP), there's no "is this camera currently assigned here" check:
    /// the /playback-thumbnail route's own directory-prefix validation already confirms filePath
    /// belongs to this node's own storage before this is ever called.</summary>
    public Task<byte[]?> CaptureThumbnailAsync(string filePath, int offsetSeconds, CancellationToken ct) =>
        ThumbnailCapture.CaptureAsync(ffmpegPath, filePath, offsetSeconds, ct);

    /// <summary>The key currently used to validate incoming live-view tokens. Seeded from the locally
    /// persisted registration (set for any node that registered after M5 shipped), then kept current
    /// by every reconcile cycle's GetConfigAsync response — the server hands this out through the
    /// same config the node already polls every 30s, specifically so a node that registered *before*
    /// M5 (and so has none stored locally) self-heals within one cycle without needing to
    /// re-register, which would mean a brand new NodeId. Volatile: read from the live endpoint's
    /// request threads, written from the reconcile loop.</summary>
    public volatile string? MediaSigningKey = registration.MediaSigningKey;

    /// <summary>The storage root the most recent reconcile resolved (config's StorageRootPath, or
    /// fallbackStorageRoot when unset) — M7's /playback-segment endpoint needs this to build the
    /// same cam-{cameraId}/main path RecordingSession writes to, without a second source of truth
    /// for where recordings live. Null until the first reconcile (live or cached-fallback)
    /// completes. Volatile for the same reason as MediaSigningKey: read from request threads,
    /// written from the reconcile loop.</summary>
    public volatile string? StorageRoot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Must happen before ReconcileLoopAsync can start any RecordingSession — see
        // _knownSegmentPaths' own doc comment for why. Best-effort: if the server can't be reached
        // yet at process startup, this falls back to an empty set, which is the exact behavior that
        // existed before this fix (not worse than before, just not yet protected this one time).
        try
        {
            _knownSegmentPaths = new HashSet<string>(await api.GetSegmentFilePathsAsync(stoppingToken), StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Fetched {Count} already-known segment path(s) before starting recording.", _knownSegmentPaths.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not fetch this node's already-known segment paths at startup — a RecordingSession started this run will treat its own on-disk history as unreported, same as before this safeguard existed.");
            _knownSegmentPaths = [];
        }

        var reconcileLoop = ReconcileLoopAsync(stoppingToken);
        var segmentReportLoop = SegmentReportLoopAsync(stoppingToken);
        var gatedDecisionLoop = GatedDecisionLoopAsync(stoppingToken);

        try
        {
            await Task.WhenAll(reconcileLoop, segmentReportLoop, gatedDecisionLoop);
        }
        catch (OperationCanceledException) { }
        finally
        {
            foreach (var recorder in _active.Values) recorder.Cts.Cancel();
            foreach (var motion in _activeMotion.Values) motion.Cts.Cancel();
            foreach (var events in _activeEvents.Values) events.Cts.Cancel();
            await Task.WhenAll(_active.Values.Select(r => r.RunTask)
                .Concat(_activeMotion.Values.Select(m => m.RunTask))
                .Concat(_activeEvents.Values.Select(e => e.RunTask)));

            // Decide every still-pending segment now, with whatever motion state is left, rather
            // than lose track of it — the motion sessions above were just cancelled, but their
            // MotionSession objects (and the LastMotionAtUtc they hold per zone) aren't cleared by
            // that, so this still reflects the freshest information actually available.
            while (_pendingGatedDecisions.TryDequeue(out var pending)) DecideGatedSegment(pending);

            await FlushSegmentsAsync(CancellationToken.None);
            await FlushStreamInfoAsync(CancellationToken.None);
            await FlushMotionSpansAsync(CancellationToken.None);
            await FlushCameraEventsAsync(CancellationToken.None);
        }
    }

    private async Task GatedDecisionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { break; }

            ProcessDueGatedDecisions();
        }
    }

    private void ProcessDueGatedDecisions()
    {
        var now = DateTime.UtcNow;
        List<PendingGatedSegmentDecision>? stillPending = null;

        while (_pendingGatedDecisions.TryDequeue(out var pending))
        {
            if (pending.DecideAtUtc > now)
            {
                (stillPending ??= []).Add(pending);
                continue;
            }
            DecideGatedSegment(pending);
        }

        if (stillPending is not null)
            foreach (var p in stillPending) _pendingGatedDecisions.Enqueue(p);
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
                PersistConfigCache(config);
                var usage = DiskSpace.TryGetUsage(storageRoot);
                var heartbeat = await api.HeartbeatAsync(new NodeHeartbeatRequest(NodeVersion.Current, usage?.FreeBytes, usage?.TotalBytes, livePort), ct);

                // Auto-update: server only ever hands this back when a genuinely newer build exists
                // for this node's platform and NodeAutoUpdate.Enabled is on (see Program.cs's
                // heartbeat handler) — nothing left to decide here except not double-triggering while
                // one is already in flight (UpdateService.IsApplying). A successful apply calls
                // IHostApplicationLifetime.StopApplication() itself, which unwinds this loop via ct.
                if (heartbeat.UpdateAvailable is not null && !updateService.IsApplying)
                {
                    _logger.LogInformation("Recorder node update available: {Version} — downloading and applying.", heartbeat.UpdateAvailable.Version);
                    await updateService.TryApplyAsync(heartbeat.UpdateAvailable, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Heartbeat/config cycle failed — will retry.");
                TryStartFromCacheIfIdle(ct);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Called only when a GetConfigAsync attempt has just failed. If nothing is recording
    /// yet — the case that matters is a fresh process start (reboot, service restart) while the
    /// central server happens to be unreachable — falls back to the last config this node
    /// successfully fetched (persisted to node.config) rather than sitting idle until the server
    /// answers. A no-op once at least one camera is already active: an already-running node
    /// surviving a *later* outage needs no help here, since a failed cycle already leaves _active
    /// untouched on its own (see Reconcile's callers above).</summary>
    private void TryStartFromCacheIfIdle(CancellationToken ct)
    {
        if (!_active.IsEmpty || _cachedConfig is null) return;

        _logger.LogWarning(
            "Server unreachable and no cameras are recording yet — starting from the last config " +
            "cached locally so this node doesn't sit idle for the rest of the outage.");
        MediaSigningKey = _cachedConfig.MediaSigningKey;
        Reconcile(_cachedConfig, ct);
    }

    /// <summary>Persists the most recent successful config to node.config (DPAPI-protected, same as
    /// the registration secret it travels with — it carries camera credentials) so a future cold
    /// start has something to fall back on if the server is unreachable at that moment. Best
    /// effort: a failure to write it shouldn't take recording down.</summary>
    private void PersistConfigCache(NodeConfigResponse config)
    {
        _cachedConfig = config;
        try
        {
            NodeConfigStore.Save(registration with { MediaSigningKey = config.MediaSigningKey, CachedConfig = config });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cached node config to disk — offline resume after a restart won't have this camera list until the next successful reconcile.");
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
            EnqueueMotionCheckpoints();
            await FlushMotionSpansAsync(ct);
            await FlushCameraEventsAsync(ct);
        }
    }

    // A little larger than the 15s tick this is called on (see SegmentReportLoopAsync) so activity
    // from just before the previous tick isn't missed on a borderline timing.
    private static readonly TimeSpan MotionCheckpointRecency = TimeSpan.FromSeconds(20);

    /// <summary>M8: without this, activity never appears in MotionSpans until a *confirmed* span
    /// closes — which two real cases both defeat: a span that stays open a long time (a genuinely
    /// active scene, or an oversensitive zone), and, just as commonly, frequent short bursts that
    /// never individually last long enough to confirm at all. The second case matters as much as
    /// the first: segment retention (MotionSession.HasMotionSince) already keys off raw activity,
    /// not confirmation, so a noisy-but-real scene gets correctly retained while the timeline shows
    /// nothing — see MotionHysteresis.CurrentInProgressSpan's doc comment. This enqueues a
    /// checkpoint snapshot of every zone with recent activity on every active camera, same queue and
    /// same report call as a closed span; NodeService.RecordMotionSpansAsync upserts by (CameraId,
    /// ZoneId, StartUtc) so repeated checkpoints extend one row instead of piling up a new one.</summary>
    private void EnqueueMotionCheckpoints()
    {
        var now = DateTime.UtcNow;
        foreach (var (cameraId, recorder) in _activeMotion)
        {
            foreach (var (zoneId, span) in recorder.Session.GetInProgressSpans(now, MotionCheckpointRecency))
            {
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(cameraId, zoneId, span.StartUtc, span.EndUtc, span.PeakScore));
            }
        }

        // M8 pass 6: same reasoning as the ServerMotion loop above, but without a recency window —
        // see CameraEventSession.CurrentInProgressSpan's doc comment for why a camera-pushed span
        // needs no "has this gone stale" check the way continuous frame-diff ticks do.
        foreach (var (cameraId, recorder) in _activeEvents)
        {
            var span = recorder.Session.CurrentInProgressSpan(now);
            if (span is not null)
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(cameraId, null, span.StartUtc, span.EndUtc, span.PeakScore));

            // M8 pass 8: same checkpointing, per configured EventTagRule, so a long-running custom
            // tag shows up on the timeline while it's still in progress instead of only once it closes.
            foreach (var (ruleId, ruleSpan) in recorder.Session.CurrentInProgressRuleSpans(now))
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(cameraId, null, ruleSpan.StartUtc, ruleSpan.EndUtc, ruleSpan.PeakScore, ruleId));
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

    private async Task FlushMotionSpansAsync(CancellationToken ct)
    {
        var batch = new List<MotionSpanReportItem>();
        while (_pendingMotionSpans.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count == 0) return;

        try
        {
            await api.ReportMotionSpansAsync(batch, ct);
            _logger.LogInformation("Reported {Count} motion span(s).", batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var item in batch) _pendingMotionSpans.Enqueue(item);
            _logger.LogWarning(ex, "Failed to report {Count} motion span(s) — will retry next cycle.", batch.Count);
        }
    }

    private async Task FlushCameraEventsAsync(CancellationToken ct)
    {
        var batch = new List<CameraEventReportItem>();
        while (_pendingCameraEvents.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count == 0) return;

        try
        {
            await api.ReportCameraEventsAsync(batch, ct);
            _logger.LogInformation("Reported {Count} camera event(s).", batch.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var item in batch) _pendingCameraEvents.Enqueue(item);
            _logger.LogWarning(ex, "Failed to report {Count} camera event(s) — will retry next cycle.", batch.Count);
        }
    }

    private string Reconcile(NodeConfigResponse config, CancellationToken stoppingToken)
    {
        var storageRoot = string.IsNullOrWhiteSpace(config.StorageRootPath) ? fallbackStorageRoot : config.StorageRootPath;
        StorageRoot = storageRoot;
        var desired = config.Cameras.ToDictionary(c => c.CameraId);

        foreach (var cameraId in _active.Keys.Except(desired.Keys).ToList())
        {
            _logger.LogInformation("Camera {CameraId} no longer assigned to this node — stopping.", cameraId);
            if (_active.TryRemove(cameraId, out var recorder)) recorder.Cts.Cancel();
        }

        foreach (var cameraId in _activeMotion.Keys.Except(desired.Keys).ToList())
        {
            _logger.LogInformation("Camera {CameraId} no longer assigned to this node — stopping motion session.", cameraId);
            if (_activeMotion.TryRemove(cameraId, out var motion)) motion.Cts.Cancel();
        }

        foreach (var cameraId in _activeEvents.Keys.Except(desired.Keys).ToList())
        {
            _logger.LogInformation("Camera {CameraId} no longer assigned to this node — stopping event session.", cameraId);
            if (_activeEvents.TryRemove(cameraId, out var events)) events.Cts.Cancel();
        }

        foreach (var camera in config.Cameras)
        {
            // Refreshed every reconcile for every camera, new or already recording — the whole
            // point is that HandleSegmentCompleted (below) must never rely on a value captured back
            // when the RecordingSession first started, since that session keeps running unchanged
            // across every later reconcile that actually updates this camera's settings.
            _latestCameraConfig[camera.CameraId] = camera;

            if (!_active.ContainsKey(camera.CameraId))
            {
                // Recording only ever uses the Main stream — Sub/Third exist for the live wall and
                // motion detection (M5/M8), not for what gets written to disk, per the plan's
                // "Main / sub stream" design.
                var mainStream = camera.Streams.FirstOrDefault(s => s.Role == "Main");
                if (mainStream is null)
                {
                    _logger.LogWarning("Camera {CameraId} ({Name}) has no Main stream — skipping.", camera.CameraId, camera.Name);
                }
                else
                {
                    var rtspUri = InjectCredentials(mainStream.RtspUri, camera.Username, camera.Password);
                    var outputDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "main");

                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var sessionLogger = loggerFactory.CreateLogger($"Recording[{camera.Name}]");
                    var session = new RecordingSession(new RecordingSessionOptions(ffmpegPath, rtspUri, outputDir), sessionLogger);
                    var capturedCameraId = camera.CameraId; // an id never goes stale the way the DTO it came from does
                    session.SegmentCompleted += segment => HandleSegmentCompleted(capturedCameraId, segment);
                    session.StreamResolutionDetected += resolution => _pendingStreamInfo.Enqueue(new StreamInfoReportItem(
                        camera.CameraId, "Main", resolution.Width, resolution.Height, resolution.Codec));

                    // See _knownSegmentPaths' own doc comment — this is the fix for the confirmed
                    // mass-discard bug. OrdinalIgnoreCase prefix match since outputDir itself is
                    // built the identical way every time (same storageRoot resolution), but real
                    // Windows/UNC paths can differ in case from run to run.
                    var knownPathsForCamera = _knownSegmentPaths?
                        .Where(p => p.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase));

                    var runTask = session.RunAsync(cts.Token, knownPathsForCamera);
                    _active[camera.CameraId] = new CameraRecorder(cts, runTask, session, rtspUri);
                    _logger.LogInformation("Started recording camera {CameraId} ({Name}) -> {OutputDir}", camera.CameraId, camera.Name, outputDir);
                }
            }

            ReconcileMotion(camera, stoppingToken);
            ReconcileEvents(camera, stoppingToken);
        }

        return storageRoot;
    }

    /// <summary>M8 pass 6/8: starts this camera's ONVIF event polling session if it advertises an
    /// Events service — independent of ReconcileMotion above (a camera can have a ServerMotion zone
    /// session, an event session, both, or neither). EventsServiceUri/credentials still get no
    /// restart-on-change handling (see the pass 6 reasoning this comment used to carry: those
    /// essentially never change for an existing camera, and a session that starts failing its next
    /// subscribe attempt naturally recovers on its own retry) — but the EventTagRule set does, the
    /// same way ReconcileMotion restarts on a changed zone signature: a rule added/edited/deleted
    /// through the admin UI needs to take effect on this session's very next reconcile, not silently
    /// wait for the camera to be reassigned.</summary>
    private void ReconcileEvents(NodeConfigCameraDto camera, CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(camera.EventsServiceUri) || !Uri.TryCreate(camera.EventsServiceUri, UriKind.Absolute, out var eventsUri))
        {
            if (_activeEvents.TryRemove(camera.CameraId, out var stopped))
            {
                _logger.LogInformation("Camera {CameraId} ({Name}) no longer advertises an Events service — stopping event session.", camera.CameraId, camera.Name);
                stopped.Cts.Cancel();
            }
            return;
        }

        var ruleSignature = BuildRuleConfigSignature(camera.EventTagRules);
        if (_activeEvents.TryGetValue(camera.CameraId, out var existing))
        {
            if (existing.RuleConfigSignature == ruleSignature) return; // already running, rules unchanged
            existing.Cts.Cancel();
            _activeEvents.TryRemove(camera.CameraId, out _);
            _logger.LogInformation("Event tag rule configuration changed for camera {CameraId} ({Name}) — restarting event session.", camera.CameraId, camera.Name);
        }

        var credentials = string.IsNullOrEmpty(camera.Username) ? null : new OnvifCredentials(camera.Username, camera.Password ?? "");
        var eventsCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var eventsLogger = loggerFactory.CreateLogger($"Events[{camera.Name}]");
        var eventSession = new CameraEventSession(eventsUri, credentials, onvifEventsClient, eventsLogger, camera.EventTagRules);

        var capturedCameraId = camera.CameraId; // same staleness reasoning as HandleSegmentCompleted's own capture
        eventSession.EventObserved += observed => _pendingCameraEvents.Enqueue(
            new CameraEventReportItem(capturedCameraId, observed.Topic, observed.UtcTime, observed.PayloadJson, observed.IsMotion));
        eventSession.MotionSpanCompleted += result => _pendingMotionSpans.Enqueue(
            new MotionSpanReportItem(capturedCameraId, null, result.StartUtc, result.EndUtc, result.PeakScore));
        eventSession.RuleSpanCompleted += (ruleId, result) => _pendingMotionSpans.Enqueue(
            new MotionSpanReportItem(capturedCameraId, null, result.StartUtc, result.EndUtc, result.PeakScore, ruleId));

        var eventsRunTask = eventSession.RunAsync(eventsCts.Token);
        _activeEvents[camera.CameraId] = new CameraEventRecorder(eventsCts, eventsRunTask, eventSession, ruleSignature);
        _logger.LogInformation("Started ONVIF event polling for camera {CameraId} ({Name}), {RuleCount} tag rule(s).", camera.CameraId, camera.Name, camera.EventTagRules.Count);
    }

    // Same content-equality-not-hash reasoning as BuildZoneConfigSignature.
    private static string BuildRuleConfigSignature(List<NodeConfigEventTagRuleDto> rules) =>
        string.Join('|', rules.OrderBy(r => r.Id).Select(r => $"{r.Id}:{r.StartTopic}:{r.StopTopic}:{r.DrivesRecording}"));

    /// <summary>M8: starts/restarts/stops this camera's motion session independently of its Main
    /// recording above — a camera can be recording fine with no motion session at all (no
    /// ServerMotion zone configured, or no Sub stream), and an already-recording camera still needs
    /// its zone config checked every reconcile even though _active already has an entry for it.</summary>
    private void ReconcileMotion(NodeConfigCameraDto camera, CancellationToken stoppingToken)
    {
        var serverMotionZones = camera.Zones.Where(z => z.Kind == nameof(ZoneKind.ServerMotion)).ToList();
        // The first real consumer of Sub in this codebase — Main is recording-only and Live's
        // auto-switch-to-Sub (plan §M5) was never actually implemented, so this path is genuinely
        // new ground, flagged as such in MotionSession's own doc comment.
        var subStream = camera.Streams.FirstOrDefault(s => s.Role == "Sub");

        if (serverMotionZones.Count == 0 || subStream is null)
        {
            if (_activeMotion.TryRemove(camera.CameraId, out var stopped))
            {
                _logger.LogInformation("Camera {CameraId} ({Name}) has no enabled ServerMotion zone with a Sub stream — stopping motion session.", camera.CameraId, camera.Name);
                stopped.Cts.Cancel();
            }
            return;
        }

        var signature = BuildZoneConfigSignature(camera.Zones);
        if (_activeMotion.TryGetValue(camera.CameraId, out var existing))
        {
            if (existing.ZoneConfigSignature == signature) return; // unchanged — leave it running
            existing.Cts.Cancel();
            _activeMotion.TryRemove(camera.CameraId, out _);
            _logger.LogInformation("Zone configuration changed for camera {CameraId} ({Name}) — restarting motion session.", camera.CameraId, camera.Name);
        }

        var subRtspUri = InjectCredentials(subStream.RtspUri, camera.Username, camera.Password);
        var motionOptions = new MotionSessionOptions(ffmpegPath, subRtspUri);

        // Ignore zones are combined once into a single exclusion mask (their union), then subtracted
        // from every ServerMotion zone's own mask — a pixel inside any Ignore zone never counts
        // toward motion for any zone on this camera, not just one it happens to overlap most.
        bool[]? ignoreMask = null;
        foreach (var z in camera.Zones.Where(z => z.Kind == nameof(ZoneKind.Ignore)))
        {
            var rasterized = ZoneRasterizer.Rasterize(ZoneRasterizer.ParsePolygon(z.PolygonJson), motionOptions.Width, motionOptions.Height);
            ignoreMask = ignoreMask is null ? rasterized : ZoneRasterizer.Union(ignoreMask, rasterized);
        }

        var zoneMasks = serverMotionZones.Select(z =>
        {
            var raw = ZoneRasterizer.Rasterize(ZoneRasterizer.ParsePolygon(z.PolygonJson), motionOptions.Width, motionOptions.Height);
            var mask = ignoreMask is null ? raw : ZoneRasterizer.Subtract(raw, ignoreMask);
            return new MotionZoneMask(z.ZoneId, mask, z.Sensitivity);
        }).ToList();

        var motionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var motionLogger = loggerFactory.CreateLogger($"Motion[{camera.Name}]");
        var motionSession = new MotionSession(motionOptions, zoneMasks, motionLogger);
        motionSession.MotionSpanCompleted += (zoneId, result) => _pendingMotionSpans.Enqueue(
            new MotionSpanReportItem(camera.CameraId, zoneId, result.StartUtc, result.EndUtc, result.PeakScore));

        var motionRunTask = motionSession.RunAsync(motionCts.Token);
        _activeMotion[camera.CameraId] = new CameraMotionRecorder(motionCts, motionRunTask, motionSession, signature);
        _logger.LogInformation("Started motion detection for camera {CameraId} ({Name}), {ZoneCount} zone(s).", camera.CameraId, camera.Name, serverMotionZones.Count);
    }

    // Content-equality signature, not a hash — the strings involved are small (a handful of zones
    // per camera at most) and readable in a debugger, so there's no reason to hash away that.
    private static string BuildZoneConfigSignature(List<NodeConfigZoneDto> zones) =>
        string.Join('|', zones.OrderBy(z => z.ZoneId).Select(z => $"{z.ZoneId}:{z.Kind}:{z.Sensitivity}:{z.PolygonJson}"));

    /// <summary>M8: decides whether a segment is worth keeping for a Motion- or Event-mode camera.
    /// Pure and unit-tested directly — the actual rule is exactly two booleans, kept separate from
    /// the file-lookup/deletion side effects in DecideGatedSegment so the rule itself can be tested
    /// without a real MotionSession or filesystem. Callers only ever reach this once the mode is
    /// already known to be Motion or Event (Continuous/Schedule never enqueue a pending decision at
    /// all — see HandleSegmentCompleted), so there's no mode parameter here any more.
    ///
    /// hasSignalSession false (no enabled ServerMotion zone/Sub stream for Motion, or no driving
    /// EventTagRule for Event — including a session that existed when the decision was *deferred*
    /// but is gone by the time it's actually made) always keeps everything, deliberately — a
    /// misconfigured or since-changed camera should behave like Continuous, not silently start
    /// deleting every segment it ever records.</summary>
    internal static bool ShouldDiscardSegment(bool hasSignalSession, bool hadSignalInWindow)
        => hasSignalSession && !hadSignalInWindow;

    /// <summary>M8 Schedule mode: true if segmentStartUtc falls inside any enabled window, evaluated
    /// against the *recording node's own local system clock* (TimeZoneInfo.Local via
    /// DateTime.ToLocalTime), not a stored per-camera/global timezone — the node is physically at
    /// the site, matching the intuitive "record 9-5" framing without inventing a timezone-config UI
    /// ahead of a dedicated NTP/timezone pass. No windows configured returns true (fail open — same
    /// "misconfigured means behave like Continuous" philosophy ShouldDiscardSegment's
    /// hasSignalSession=false case uses).
    ///
    /// A window that crosses midnight (EndTime &lt; StartTime, e.g. 22:00-02:00) is attributed to
    /// the day it *starts* on — a segment landing in its early-morning tail has today's calendar day
    /// (e.g. Saturday) but needs to match against *yesterday's* day flag (Friday) to correctly count
    /// as "still inside Friday night's window," not today's. Checking both today's and yesterday's
    /// flag (split across the two branches below) is what makes that work without a lookup table.</summary>
    internal static bool IsWithinSchedule(IReadOnlyList<NodeConfigScheduleWindowDto> windows, DateTime segmentStartUtc)
    {
        if (windows.Count == 0) return true;

        var local = segmentStartUtc.ToLocalTime();
        var today = ToDayFlag(local.DayOfWeek);
        var yesterday = ToDayFlag(local.DayOfWeek == DayOfWeek.Sunday ? DayOfWeek.Saturday : local.DayOfWeek - 1);
        var time = TimeOnly.FromDateTime(local);

        foreach (var w in windows)
        {
            if (!Enum.TryParse<DayOfWeekFlags>(w.Days, out var days)) continue;

            if (w.EndTime > w.StartTime)
            {
                if (days.HasFlag(today) && time >= w.StartTime && time < w.EndTime) return true;
            }
            else
            {
                // Crosses midnight (or StartTime == EndTime, treated as "all day"): the window
                // belongs to the day it starts, so the early tail needs yesterday's flag.
                if (days.HasFlag(today) && time >= w.StartTime) return true;
                if (days.HasFlag(yesterday) && time < w.EndTime) return true;
            }
        }
        return false;
    }

    private static DayOfWeekFlags ToDayFlag(DayOfWeek d) => (DayOfWeekFlags)(1 << (int)d);

    /// <summary>Every camera's RecordingSession.SegmentCompleted handler. Continuous cameras (the
    /// default, and every camera before this feature existed) always report immediately, same as
    /// before this feature existed at all.
    ///
    /// Schedule mode is decided right here, synchronously — unlike Motion/Event, there's nothing to
    /// retroactively claim: whether segment.StartUtc fell inside a configured window is fully known
    /// the instant the segment completes, so deferring it through the same pending-decision queue
    /// Motion/Event use would only add latency for no benefit.
    ///
    /// Motion and Event both take the deferred path, via ProcessDueGatedDecisions/DecideGatedSegment
    /// below — Event mode gates on *only* its driving EventTagRule signal (a ServerMotion zone or
    /// the built-in ONVIF motion classifier contributes nothing to Event mode, unlike Motion mode's
    /// OR-chain of every signal source), so "has a signal source" means something different per mode
    /// and is computed separately for each below.
    ///
    /// Takes only cameraId, not a NodeConfigCameraDto — RecordingSession.SegmentCompleted is wired
    /// up exactly once, the moment a camera's recording first starts, and keeps firing for as long
    /// as that same session runs (hours to days), so a DTO captured at wire-up time would silently
    /// go stale the instant the operator changed Recording Mode or the pre/post-roll seconds on an
    /// already-recording camera — confirmed live as a real bug, not a hypothetical one: three
    /// cameras set to Motion mode had every segment retained with zero gaps for 35+ hours, because
    /// the gating check below was still reading "Continuous" from whenever each session first
    /// started. _latestCameraConfig is refreshed every 30s reconcile for every camera, so this
    /// always reads whatever is actually configured right now.</summary>
    private void HandleSegmentCompleted(Guid cameraId, RecordingSegment segment)
    {
        if (!_latestCameraConfig.TryGetValue(cameraId, out var camera))
        {
            // Shouldn't happen — Reconcile populates this before a session can even start — but if
            // it somehow does, report normally rather than risk discarding real footage with no
            // config to judge it against.
            _logger.LogWarning("No cached config for camera {CameraId} when its segment completed — reporting it rather than risking a discard with no config to judge it against.", cameraId);
            ReportSegment(cameraId, null, segment);
            return;
        }

        var mainStream = camera.Streams.FirstOrDefault(s => s.Role == "Main");
        if (!Enum.TryParse<RecordingMode>(camera.RecordingMode, ignoreCase: true, out var mode))
            mode = RecordingMode.Continuous; // unrecognized string shouldn't happen (dropdown-constrained) — fail open, same philosophy as every other gap here

        if (mode == RecordingMode.Continuous)
        {
            ReportSegment(cameraId, mainStream, segment);
            return;
        }

        if (mode == RecordingMode.Schedule)
        {
            if (camera.ScheduleWindows.Count == 0 && _warnedScheduleModeMissingWindows.TryAdd(cameraId, 0))
            {
                _logger.LogWarning(
                    "Camera {CameraId} ({Name}) is set to Schedule recording mode but has no schedule window " +
                    "configured — recording every segment as if it were Continuous until one is added. Not " +
                    "treated as a discard-everything condition.", cameraId, camera.Name);
            }

            if (IsWithinSchedule(camera.ScheduleWindows, segment.StartUtc)) ReportSegment(cameraId, mainStream, segment);
            else DiscardSegment(segment, cameraId, camera.Name, "started outside every configured schedule window");
            return;
        }

        // Motion or Event from here down.
        var hasMotionSession = _activeMotion.ContainsKey(cameraId);
        var hasEventSession = _activeEvents.ContainsKey(cameraId);
        // M8 pass 6: either signal source counts for Motion — a camera relying only on its own
        // onboard detection (no ServerMotion zone drawn at all) must still gate correctly, not just
        // one with a server-side zone. Event mode is narrower: only a *driving* EventTagRule counts,
        // since Event mode's whole point is "record only for this specific tagged trigger," not "any
        // activity" — an event session with no DrivesRecording=true rule contributes nothing, same
        // as having no session at all. See DecideGatedSegment for the matching hadSignalInWindow
        // check per mode.
        var hasSignalSource = mode == RecordingMode.Event
            ? hasEventSession && camera.EventTagRules.Any(r => r.DrivesRecording)
            : hasMotionSession || hasEventSession;

        if (!hasSignalSource)
        {
            var warned = mode == RecordingMode.Event ? _warnedEventModeMissingRule : _warnedMotionModeMissingSession;
            if (warned.TryAdd(cameraId, 0))
            {
                var reason = mode == RecordingMode.Event
                    ? "no enabled event tag rule with \"Drives recording\" checked"
                    : "no enabled ServerMotion zone with a Sub stream, and no ONVIF event subscription, to detect motion from";
                _logger.LogWarning(
                    "Camera {CameraId} ({Name}) is set to {Mode} recording mode but has {Reason} — recording " +
                    "every segment as if it were Continuous until one is available. Not treated as a " +
                    "discard-everything condition.", cameraId, camera.Name, mode, reason);
            }

            ReportSegment(cameraId, mainStream, segment);
            return;
        }

        // Deferred, not decided now — see PendingGatedSegmentDecision's doc comment for why: a
        // motion/event that starts shortly after this segment ends can still retroactively claim it
        // as pre-roll, and that can't be known until PreRoll seconds have actually passed.
        _pendingGatedDecisions.Enqueue(new PendingGatedSegmentDecision(
            cameraId, camera.Name, segment, mainStream, mode, camera.MotionPostRollSeconds,
            segment.EndUtc.AddSeconds(camera.MotionPreRollSeconds)));
    }

    /// <summary>Makes the final call on one deferred Motion- or Event-mode segment, once its PreRoll
    /// horizon has passed (or immediately, at shutdown — see ExecuteAsync's finally block).
    /// Re-resolves the camera's motion/event session fresh rather than trusting anything captured at
    /// enqueue time, since real time has passed and the session could have been removed (zone
    /// deleted, camera reassigned) in the meantime — ShouldDiscardSegment's hasSignalSession=false
    /// fallback handles that safely.
    ///
    /// A discarded segment was never reported, so it never got a Segments row — deleting the file
    /// here is the only cleanup needed; there's nothing for StorageManager or the web tier to know
    /// about. Deliberately best-effort: a delete failure is logged, not retried or escalated — the
    /// file just lingers on disk until a real retention sweep eventually claims it, which is a far
    /// safer failure mode than treating a stray file as something worth crashing recording over.</summary>
    private void DecideGatedSegment(PendingGatedSegmentDecision pending)
    {
        var hasMotionSession = _activeMotion.TryGetValue(pending.CameraId, out var motionRecorder);
        var hasEventSession = _activeEvents.TryGetValue(pending.CameraId, out var eventRecorder);
        // Anchored to the segment's START, not its end — see HasMotionSince's doc comment for why
        // anchoring to EndUtc silently discarded segments with real motion in them once PostRoll
        // (an operator-configurable, potentially small value) was shorter than "how far before the
        // segment's end the motion happened."
        var windowStart = pending.Segment.StartUtc.AddSeconds(-pending.PostRollSeconds);

        bool hasSignalSession;
        bool hadSignalInWindow;

        if (pending.Mode == RecordingMode.Event)
        {
            // Only the driving-rule signal counts — see HandleSegmentCompleted's own comment on why
            // Event mode is narrower than Motion mode's OR-chain.
            hasSignalSession = hasEventSession;
            hadSignalInWindow = hasEventSession &&
                (eventRecorder!.Session.AnyDrivingRuleHasMotionSince(windowStart) || eventRecorder.Session.AnyDrivingRuleActive);
        }
        else // Motion
        {
            // Checked against BOTH signal sources (M8 pass 6) — a segment is kept if *either* the
            // server-side zone or the camera's own pushed events saw activity in the window, not
            // just whichever one happens to be configured. For the event session specifically,
            // HasMotionSince alone isn't enough — see CameraEventSession.IsMotionActive's doc
            // comment: many ONVIF implementations send exactly one notification per edge (rising,
            // then nothing again until falling), so LastMotionAtUtc goes stale relative to
            // windowStart for a segment decided well into a long, sparsely-reported event even
            // though motion never actually stopped. IsMotionActive has no timeout of its own — it's
            // only false once an actual falling-edge notification closes the span — so ORing it in
            // here is what makes recording keep being retained for as long as the camera hasn't said
            // motion stopped, not until some arbitrary staleness window expires. M8 pass 8: a
            // DrivesRecording=true EventTagRule is one more signal source alongside the built-in
            // classifier and the server-side zone for Motion mode specifically — a purely-tagging
            // rule (DrivesRecording=false) deliberately never reaches here, see
            // AnyDrivingRuleHasMotionSince/AnyDrivingRuleActive's own doc comments.
            hasSignalSession = hasMotionSession || hasEventSession;
            hadSignalInWindow =
                (hasMotionSession && motionRecorder!.Session.HasMotionSince(windowStart)) ||
                (hasEventSession && (
                    eventRecorder!.Session.HasMotionSince(windowStart) || eventRecorder.Session.IsMotionActive ||
                    eventRecorder.Session.AnyDrivingRuleHasMotionSince(windowStart) || eventRecorder.Session.AnyDrivingRuleActive));
        }

        if (ShouldDiscardSegment(hasSignalSession, hadSignalInWindow))
        {
            DiscardSegment(pending.Segment, pending.CameraId, pending.CameraName, "no activity within the pre/post-roll window");
            return;
        }

        ReportSegment(pending.CameraId, pending.MainStream, pending.Segment);
    }

    /// <summary>Shared discard side effect for every gated mode (Schedule's immediate discard in
    /// HandleSegmentCompleted, Motion/Event's deferred one in DecideGatedSegment) — deletes the
    /// already-recorded file, logs at Debug (routine, not a problem), and best-effort-only on a
    /// delete failure (see DecideGatedSegment's own doc comment for why that's the safe choice).</summary>
    private void DiscardSegment(RecordingSegment segment, Guid cameraId, string cameraName, string reason)
    {
        try
        {
            File.Delete(segment.FilePath);
            _logger.LogDebug("Discarded segment {FilePath} for camera {CameraId} ({Name}) — {Reason}.",
                segment.FilePath, cameraId, cameraName, reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete discarded segment {FilePath} for camera {CameraId} — it will linger until a retention sweep claims it.",
                segment.FilePath, cameraId);
        }
    }

    // mainStream is nullable: it's looked up fresh at report time (see HandleSegmentCompleted), not
    // captured once — a camera's Main stream can theoretically be removed/disabled in the time
    // between when its recording session started and when a given segment is decided. Falls back
    // to unknown codec/resolution/audio rather than dropping the segment report entirely; the file
    // and its timing are still real regardless of whether this metadata is available.
    private void ReportSegment(Guid cameraId, NodeConfigStreamDto? mainStream, RecordingSegment segment) =>
        _pendingSegments.Enqueue(new SegmentReportItem(
            cameraId, "Main", segment.StartUtc, segment.EndUtc, segment.FilePath, segment.SizeBytes,
            mainStream?.Codec, mainStream?.Width, mainStream?.Height, mainStream?.HasAudio ?? false));

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
