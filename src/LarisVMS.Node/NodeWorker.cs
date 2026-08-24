using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core;
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

    // M18: adaptive streaming's Sub live sessions — same concurrency reasoning as _active above (read
    // from Program.cs's /live WebSocket endpoint on request threads, written only from the reconcile
    // loop).
    private readonly ConcurrentDictionary<Guid, CameraLiveSubRecorder> _activeLiveSub = new();

    // M8 pass 6: same shape as _activeMotion/_pendingMotionSpans above, for ONVIF PullPoint camera
    // events instead of server-side substream frame-diffing — a camera can have either signal
    // source, both, or neither, independently (see ReconcileEvents and DecideMotionSegment's use of
    // both dictionaries together).
    private readonly ConcurrentDictionary<Guid, CameraEventRecorder> _activeEvents = new();
    private readonly ConcurrentDictionary<Guid, CameraIntegrationRecorder> _activeIntegrations = new();
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

    // M17: probed once at startup (see ExecuteAsync) and reported on every heartbeat thereafter —
    // hardware and the installed ffmpeg build don't change while this process is running, so there's
    // no reason to re-run FfmpegCapabilityProber on a schedule. Null until that first probe
    // completes (heartbeat reports whatever it has; a null DetectedEncoders leaves the server's
    // previously-stored value alone rather than clearing it — see NodeService.RecordHeartbeatAsync).
    private IReadOnlyList<string>? _detectedEncoders;

    /// <summary>The active RecordingSession for a camera this node is currently recording, or null
    /// if it isn't assigned here (or isn't recording yet). Used by the live-view WebSocket endpoint
    /// to attach a viewer to the right session's tee'd live fanout.</summary>
    public RecordingSession? TryGetSession(Guid cameraId) => _active.TryGetValue(cameraId, out var r) ? r.Session : null;

    // M18 follow-up: the /playback-segment route's partial-fetch fragment index (see
    // Program.cs) — keyed on full file path since a completed segment file never changes, so its
    // index never goes stale. Crude bulk-clear-on-overflow bound rather than a real LRU, same
    // simplicity trade-off SettingsResolver's own cache already makes: this node's own segments
    // rarely have more than a few hundred actively scrubbed-through at once, and a cleared cache
    // just costs one extra rebuild on its next miss, not a correctness problem.
    private readonly ConcurrentDictionary<string, IReadOnlyList<Mp4Fragment>> _fragmentIndexCache = new();
    private const int MaxFragmentIndexCacheEntries = 500;

    /// <summary>Builds (or reuses a cached) byte-offset/media-time index for one segment file — see
    /// Mp4FragmentIndexer's own doc comment for how. Retries a few times (StorageRetry) on a
    /// transient storage I/O error — confirmed live as a real need on this fleet's SMB-backed storage
    /// — before giving up; a final failure returns an empty list and is deliberately NOT cached, so
    /// the next request tries again rather than being stuck treating a transient failure as "this
    /// file has no fragments" forever.</summary>
    public async Task<IReadOnlyList<Mp4Fragment>> GetOrBuildFragmentIndexAsync(string fullPath, CancellationToken ct)
    {
        if (_fragmentIndexCache.TryGetValue(fullPath, out var cached)) return cached;
        if (_fragmentIndexCache.Count > MaxFragmentIndexCacheEntries) _fragmentIndexCache.Clear();

        IReadOnlyList<Mp4Fragment> fragments;
        try
        {
            fragments = await StorageRetry.ExecuteAsync(_logger, $"Fragment index build for {fullPath}", () =>
            {
                using var fs = File.OpenRead(fullPath);
                return Task.FromResult<IReadOnlyList<Mp4Fragment>>(Mp4FragmentIndexer.Build(fs));
            }, ct);
        }
        catch (IOException)
        {
            return [];
        }

        _fragmentIndexCache[fullPath] = fragments;
        return fragments;
    }

    /// <summary>The active Sub live session for a camera, if adaptive streaming is enabled and one is
    /// running — null covers every reason it might not be (toggle off, camera has no enabled Sub
    /// stream, not assigned here, session hasn't started yet) identically, since the /live endpoint's
    /// only reaction to any of them is the same: fall back to Main.</summary>
    public ILiveSource? TryGetLiveSubSession(Guid cameraId) => _activeLiveSub.TryGetValue(cameraId, out var r) ? r.Session : null;

    /// <summary>Opens a short-lived RTSP session against the camera's Main stream and grabs one
    /// frame — null if the camera isn't assigned to this node (nothing to grab from) or the grab
    /// itself fails (camera unreachable, auth failure, timeout — see SnapshotCapture).</summary>
    public Task<byte[]?> CaptureSnapshotAsync(Guid cameraId, CancellationToken ct) =>
        _active.TryGetValue(cameraId, out var recorder)
            ? SnapshotCapture.CaptureAsync(ffmpegPath, recorder.RtspUri, ct)
            : Task.FromResult<byte[]?>(null);

    // Concurrency caps for M7 pass 2 hover-thumbnail extraction — a real, confirmed risk without
    // this: each cache-miss spawns its own ffmpeg process, and a fast sweep across an uncached
    // stretch of timeline could otherwise fire off dozens of them at once, competing with this
    // node's own live recording ffmpeg processes for CPU/disk. On-demand (hover) and background
    // (catch-up) requests get separate, small gates rather than sharing one pool — background
    // generation must never be able to starve a user actively waiting on a hover preview, and the
    // reverse (a hover burst) shouldn't be able to fully starve backfill either.
    private static readonly SemaphoreSlim OnDemandThumbnailGate = new(2, 2);
    private static readonly SemaphoreSlim BackgroundThumbnailGate = new(1, 1);

    /// <summary>M7 pass 2: extracts one JPEG frame from an already-recorded segment file — unlike
    /// CaptureSnapshotAsync (live RTSP), there's no "is this camera currently assigned here" check:
    /// the /playback-thumbnail route's own directory-prefix validation already confirms filePath
    /// belongs to this node's own storage before this is ever called. Bounded by
    /// OnDemandThumbnailGate — a hover request that can't get a slot within 3s gives up (the caller
    /// renders "No preview available") rather than piling up behind an unbounded queue for a preview
    /// that may already be stale by the time it'd be served.</summary>
    public async Task<byte[]?> CaptureThumbnailAsync(string filePath, int offsetSeconds, CancellationToken ct, int maxDimension = ThumbnailCapture.DefaultMaxDimension, int quality = ThumbnailCapture.DefaultQuality)
    {
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        gateCts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await OnDemandThumbnailGate.WaitAsync(gateCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // gate stayed full for 3s straight — treat like any other capture failure
        }
        try
        {
            return await ThumbnailCapture.CaptureAsync(ffmpegPath, filePath, offsetSeconds, ct, maxDimension: maxDimension, quality: quality);
        }
        finally
        {
            OnDemandThumbnailGate.Release();
        }
    }

    /// <summary>Same extraction, for ThumbnailBackfillService's low-priority background catch-up
    /// loop instead of a live hover request — its own smaller gate (one at a time) plus BelowNormal
    /// OS process priority (see ThumbnailCapture.CaptureAsync's lowPriority parameter) so it never
    /// meaningfully competes with live recording or an on-demand hover. Waits for a slot rather than
    /// giving up on a timeout, since nothing is blocked synchronously waiting on this.</summary>
    public async Task<byte[]?> CaptureThumbnailInBackgroundAsync(string filePath, int offsetSeconds, CancellationToken ct)
    {
        await BackgroundThumbnailGate.WaitAsync(ct);
        try
        {
            return await ThumbnailCapture.CaptureAsync(ffmpegPath, filePath, offsetSeconds, ct, lowPriority: true);
        }
        finally
        {
            BackgroundThumbnailGate.Release();
        }
    }

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

        // M17: best-effort, same reasoning as every other probe on this path — a failed/empty result
        // (FfmpegCapabilityProber never throws) just means this node reports no detected encoders
        // this run rather than blocking startup on it.
        _detectedEncoders = await FfmpegCapabilityProber.ProbeAsync(ffmpegPath, stoppingToken);
        _logger.LogInformation("Detected encoder(s): {Encoders}",
            _detectedEncoders.Count > 0 ? string.Join(", ", _detectedEncoders) : "(none — ffmpeg -encoders probe found nothing recognized, or failed)");

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
            foreach (var integration in _activeIntegrations.Values) integration.Cts.Cancel();
            foreach (var liveSub in _activeLiveSub.Values) liveSub.Cts.Cancel();
            await Task.WhenAll(_active.Values.Select(r => r.RunTask)
                .Concat(_activeMotion.Values.Select(m => m.RunTask))
                .Concat(_activeEvents.Values.Select(e => e.RunTask))
                .Concat(_activeIntegrations.Values.Select(i => i.RunTask))
                .Concat(_activeLiveSub.Values.Select(l => l.RunTask)));

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
                var heartbeat = await api.HeartbeatAsync(new NodeHeartbeatRequest(NodeVersion.Current, usage?.FreeBytes, usage?.TotalBytes, livePort,
                    DateTime.UtcNow, _detectedEncoders?.ToList()), ct);

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
            catch (Exception ex) when (IsRetryable(ex, ct))
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

    /// <summary>
    /// Whether an exception caught around a node→web call is a transient failure worth logging and
    /// retrying, versus this node genuinely shutting down.
    ///
    /// The distinction is load-bearing and was a real, confirmed multi-hour outage: these catches used
    /// to filter on <c>ex is not OperationCanceledException</c> alone, but an <see cref="HttpClient"/>
    /// timeout (NodeApiClient sets 15s) throws <see cref="TaskCanceledException"/>, which *derives
    /// from* OperationCanceledException — so a single slow/black-holed request escaped the catch
    /// entirely, faulted <see cref="SegmentReportLoopAsync"/>, and killed all node→web reporting until
    /// the service was restarted. Nothing noticed, because <c>Task.WhenAll</c> in ExecuteAsync only
    /// completes once *every* loop finishes: the other two loops kept running, so the fault sat
    /// unobserved (and was then swallowed by ExecuteAsync's own OperationCanceledException catch at
    /// shutdown, so it never even reached the log). Meanwhile recording carried on writing files
    /// perfectly — the segments simply never got reported, leaving hours of footage on disk that
    /// Playback shows as a gap. Confirmed on nvr1, 2026-08-21 16:36→18:56 (2h20m).
    ///
    /// Checking the token rather than the exception type is what separates the two cases: on a real
    /// shutdown our own <paramref name="ct"/> is cancelled and the exception should propagate (ending
    /// the loop cleanly); on an HTTP timeout it isn't, so this is just another retryable failure.
    /// </summary>
    internal static bool IsRetryable(Exception ex, CancellationToken ct)
        => ex is not OperationCanceledException || !ct.IsCancellationRequested;

    private async Task SegmentReportLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { break; }

            // Belt and braces alongside IsRetryable above: every Flush below already handles its own
            // failures, but this loop going down takes *all* node→web reporting with it and can't
            // recover without a service restart (see IsRetryable's own doc comment for the outage that
            // proved it), so nothing unanticipated gets to end it either. A tick that fails wholesale
            // is logged and retried on the next one rather than being the last tick that ever runs.
            try
            {
                await FlushSegmentsAsync(ct);
                EnqueueHealthReports();
                await FlushStreamInfoAsync(ct);
                EnqueueMotionCheckpoints();
                await FlushMotionSpansAsync(ct);
                await FlushCameraEventsAsync(ct);
            }
            catch (Exception ex) when (IsRetryable(ex, ct))
            {
                _logger.LogError(ex, "Report cycle failed unexpectedly — will retry next cycle.");
            }
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
    /// <summary>M11: refreshes every active camera's real-time fps/bitrate/reconnect-count on the
    /// same 15s tick EnqueueMotionCheckpoints already uses for its own periodic snapshot — unlike
    /// StreamResolutionDetected (fires once per connection), this runs every tick regardless of
    /// whether anything changed, so the dashboard's numbers actually move. Width/Height/Codec are
    /// left null here (this loop has no cheap way to know the current values outside the
    /// StreamResolutionDetected closure) — UpdateStreamInfoAsync's coalesce-preserve update keeps
    /// whatever was last reported for those instead of clobbering them with null.</summary>
    private void EnqueueHealthReports()
    {
        foreach (var (cameraId, recorder) in _active)
        {
            var session = recorder.Session;
            _pendingStreamInfo.Enqueue(new StreamInfoReportItem(
                cameraId, "Main", Width: null, Height: null, Codec: null,
                Fps: session.CurrentFps, BitrateKbps: session.CurrentBitrateKbps, ReconnectCount: session.TotalReconnectCount));
        }
    }

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

            // Same again per detected object class — a person standing in frame for two minutes
            // should appear on the timeline as it happens, not only once they leave.
            foreach (var (kind, detectionSpan) in recorder.Session.CurrentInProgressDetectionSpans(now))
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(cameraId, null, detectionSpan.StartUtc,
                    detectionSpan.EndUtc, detectionSpan.PeakScore, EventTagRuleId: null, DetectionKind: kind));
        }

        // Vendor-plugin detections checkpoint identically — on this fleet they're the *only* source
        // of object classes, since these cameras never publish them over ONVIF at all.
        foreach (var (cameraId, recorder) in _activeIntegrations)
        {
            foreach (var (kind, detectionSpan) in recorder.Session.CurrentInProgressDetectionSpans(now))
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(cameraId, null, detectionSpan.StartUtc,
                    detectionSpan.EndUtc, detectionSpan.PeakScore, EventTagRuleId: null, DetectionKind: kind));
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
        catch (Exception ex) when (IsRetryable(ex, ct))
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
        catch (Exception ex) when (IsRetryable(ex, ct))
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
        catch (Exception ex) when (IsRetryable(ex, ct))
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
        catch (Exception ex) when (IsRetryable(ex, ct))
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

        foreach (var cameraId in _activeIntegrations.Keys.Except(desired.Keys).ToList())
        {
            _logger.LogInformation("Camera {CameraId} no longer assigned to this node — stopping its integration.", cameraId);
            if (_activeIntegrations.TryRemove(cameraId, out var integration)) integration.Cts.Cancel();
        }

        // M18: also stops every running Sub live session outright when the toggle itself is off, not
        // just ones for cameras no longer assigned — see ReconcileLiveSub's own doc comment for why
        // this can't just be "skip starting new ones."
        var liveSubKeysToStop = config.AdaptiveStreamingEnabled
            ? _activeLiveSub.Keys.Except(desired.Keys).ToList()
            : _activeLiveSub.Keys.ToList();
        foreach (var cameraId in liveSubKeysToStop)
        {
            _logger.LogInformation("Stopping Sub live session for camera {CameraId} ({Reason}).", cameraId,
                config.AdaptiveStreamingEnabled ? "no longer assigned to this node" : "adaptive streaming disabled");
            if (_activeLiveSub.TryRemove(cameraId, out var liveSub)) liveSub.Cts.Cancel();
        }

        foreach (var camera in config.Cameras)
        {
            // Refreshed every reconcile for every camera, new or already recording — the whole
            // point is that HandleSegmentCompleted (below) must never rely on a value captured back
            // when the RecordingSession first started, since that session keeps running unchanged
            // across every later reconcile that actually updates this camera's settings.
            _latestCameraConfig[camera.CameraId] = camera;

            // M18: a Privacy zone added/edited/removed on an already-recording camera needs the
            // session restarted to pick up the new (or now-absent) `-vf drawbox` filters — burning a
            // mask in is baked into the encoded stream at record time, not something a later reconcile
            // can silently change underneath an already-running ffmpeg process the way, say, retention
            // days can. Same signature-and-restart shape as ReconcileMotion's own zone-change handling.
            //
            // PrivacyMaskEnabled is a deliberate kill switch, off for now: confirmed live on real
            // Intel/NVIDIA hardware that a masked camera gets stuck cycling Connecting/Backoff forever
            // (see CHANGELOG 0.111.0/0.112.0) — one real cause (pairing decode hwaccel with the CPU
            // drawbox filter) was found and fixed in 0.112.0, but that did not resolve the reported
            // symptom, and the actual root cause is still unknown. Rather than leave a Privacy zone as
            // an attractive nuisance that breaks a camera's recording the moment one is saved, this
            // flag makes it a safe no-op again — same as CameraMotion's own "drawable, saved, does
            // nothing yet" state — until the real bug is found. Every helper below
            // (PrivacyMaskFilterBuilder, EncoderSelection, RecordingSession's transcode branch, and
            // their tests) is untouched and ready for whenever that happens; only this one switch and
            // the effectiveZones substitution below need to change back.
            const bool PrivacyMaskEnabled = false;
            var effectiveZones = PrivacyMaskEnabled ? camera.Zones : [];
            var privacyEncoder = ChoosePrivacyEncoder(effectiveZones);
            var privacySignature = BuildPrivacyMaskSignature(effectiveZones, privacyEncoder);

            // Recording only ever uses the Main stream — Sub/Third exist for the live wall and
            // motion detection (M5/M8), not for what gets written to disk, per the plan's
            // "Main / sub stream" design. Computed up front (not just inside the "start fresh"
            // branch below) so an already-running session's RTSP URI can be compared against the
            // camera's current one every reconcile — mirrors ReconcileLiveSub's own
            // StreamSignature comparison for the Sub stream, which this Main-stream branch was
            // missing: editing a camera's ONVIF Device Service URI re-probes and rewrites
            // CameraStream.RtspUri, but without this check an already-recording camera never
            // noticed and kept running the old ffmpeg process against the stale URL forever.
            var mainStream = camera.Streams.FirstOrDefault(s => s.Role == "Main");
            var rtspUri = mainStream is null ? null : InjectCredentials(mainStream.RtspUri, camera.Username, camera.Password);

            if (_active.TryGetValue(camera.CameraId, out var activeRecorder) && activeRecorder.PrivacyMaskSignature != privacySignature)
            {
                _logger.LogInformation("Privacy mask configuration changed for camera {CameraId} ({Name}) — restarting recording session.", camera.CameraId, camera.Name);
                activeRecorder.Cts.Cancel();
                _active.TryRemove(camera.CameraId, out _);
            }
            else if (activeRecorder is not null && activeRecorder.SegmentSeconds != camera.SegmentSeconds)
            {
                _logger.LogInformation("Segment length changed for camera {CameraId} ({Name}) — restarting recording session ({Old}s -> {New}s).",
                    camera.CameraId, camera.Name, activeRecorder.SegmentSeconds, camera.SegmentSeconds);
                activeRecorder.Cts.Cancel();
                _active.TryRemove(camera.CameraId, out _);
            }
            else if (activeRecorder is not null && rtspUri is not null && activeRecorder.RtspUri != rtspUri)
            {
                _logger.LogInformation("Main stream URL changed for camera {CameraId} ({Name}) — restarting recording session.", camera.CameraId, camera.Name);
                activeRecorder.Cts.Cancel();
                _active.TryRemove(camera.CameraId, out _);
            }

            if (!_active.ContainsKey(camera.CameraId))
            {
                if (mainStream is null)
                {
                    _logger.LogWarning("Camera {CameraId} ({Name}) has no Main stream — skipping.", camera.CameraId, camera.Name);
                }
                else
                {
                    var outputDir = Path.Combine(storageRoot, $"cam-{camera.CameraId}", "main");

                    var privacyMaskFilters = BuildPrivacyMaskFilters(effectiveZones);
                    // privacyEncoder can be null here (capability probe hasn't completed/found nothing
                    // yet) without meaning "record unmasked" — RecordingSession's own BuildCodecArgs
                    // falls back to libx264 (software, always available) whenever filters are non-empty
                    // but no specific encoder was chosen, so a Privacy zone is never silently skipped
                    // just because the hardware probe hasn't reported in by the time this camera
                    // started recording. Once it does, the mask-signature check above restarts this
                    // session and picks up the real hardware encoder then.
                    if (privacyMaskFilters.Count > 0 && privacyEncoder is null)
                    {
                        _logger.LogInformation("Camera {CameraId} ({Name}) has {Count} enabled Privacy zone(s) — masking with software libx264 until this node's hardware-encoder probe reports in.",
                            camera.CameraId, camera.Name, privacyMaskFilters.Count);
                    }

                    var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var sessionLogger = loggerFactory.CreateLogger($"Recording[{camera.Name}]");
                    var session = new RecordingSession(new RecordingSessionOptions(ffmpegPath, rtspUri!, outputDir,
                        SegmentSeconds: camera.SegmentSeconds,
                        PrivacyMaskFilters: privacyMaskFilters.Count > 0 ? privacyMaskFilters : null, VideoEncoder: privacyEncoder), sessionLogger);
                    var capturedCameraId = camera.CameraId; // an id never goes stale the way the DTO it came from does
                    session.SegmentCompleted += segment => HandleSegmentCompleted(capturedCameraId, segment);
                    session.StreamResolutionDetected += resolution => _pendingStreamInfo.Enqueue(new StreamInfoReportItem(
                        camera.CameraId, "Main", resolution.Width, resolution.Height, resolution.Codec));
                    // Its own report rather than fields on the resolution one: ffmpeg prints the
                    // video and audio stream lines separately (and a video-only camera never prints
                    // the audio one at all), so there's no single moment where both are known.
                    // UpdateStreamInfoAsync's coalesce-preserve update is what lets two partial
                    // reports build up one complete row.
                    session.StreamAudioDetected += audio => _pendingStreamInfo.Enqueue(new StreamInfoReportItem(
                        camera.CameraId, "Main", Width: null, Height: null, Codec: null,
                        AudioCodec: audio.Codec, AudioSampleRateHz: audio.SampleRateHz));

                    // See _knownSegmentPaths' own doc comment — this is the fix for the confirmed
                    // mass-discard bug. OrdinalIgnoreCase prefix match since outputDir itself is
                    // built the identical way every time (same storageRoot resolution), but real
                    // Windows/UNC paths can differ in case from run to run.
                    //
                    // Unioned with a live scan of outputDir, not just the process-startup snapshot:
                    // M18's privacy-mask restart above can replace an already-running session's
                    // RecordingSession mid-process-lifetime, and whatever files the *old* session had
                    // already reported (from *its* own in-memory reportedPaths, which this new session
                    // has no way to see) are sitting on disk right now — without this, the new
                    // session's fresh, empty reportedPaths would rediscover and re-report every one of
                    // them as if brand new, the exact duplicate-segment bug knownSegmentPaths already
                    // exists to prevent, just via a second path into it. Harmless (a no-op superset)
                    // for a camera's genuine first start in this process's lifetime, where
                    // _knownSegmentPaths already covers the same files.
                    IEnumerable<string> onDiskNow;
                    try { onDiskNow = Directory.GetFiles(outputDir, "*.mp4", SearchOption.AllDirectories); }
                    catch (IOException) { onDiskNow = []; } // includes DirectoryNotFoundException — a camera's first-ever start has no folder yet
                    var knownPathsForCamera = (_knownSegmentPaths?
                        .Where(p => p.StartsWith(outputDir, StringComparison.OrdinalIgnoreCase)) ?? [])
                        .Union(onDiskNow, StringComparer.OrdinalIgnoreCase);

                    var runTask = session.RunAsync(cts.Token, knownPathsForCamera);
                    _active[camera.CameraId] = new CameraRecorder(cts, runTask, session, rtspUri!, privacySignature, camera.SegmentSeconds);
                    _logger.LogInformation("Started recording camera {CameraId} ({Name}) -> {OutputDir}", camera.CameraId, camera.Name, outputDir);
                }
            }

            ReconcileMotion(camera, stoppingToken);
            ReconcileEvents(camera, stoppingToken);
            ReconcileIntegration(camera, stoppingToken);
            if (config.AdaptiveStreamingEnabled) ReconcileLiveSub(camera, stoppingToken);
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
        eventSession.DetectionSpanCompleted += (kind, result) => _pendingMotionSpans.Enqueue(
            new MotionSpanReportItem(capturedCameraId, null, result.StartUtc, result.EndUtc, result.PeakScore,
                EventTagRuleId: null, DetectionKind: kind));

        var eventsRunTask = eventSession.RunAsync(eventsCts.Token);
        _activeEvents[camera.CameraId] = new CameraEventRecorder(eventsCts, eventsRunTask, eventSession, ruleSignature);
        _logger.LogInformation("Started ONVIF event polling for camera {CameraId} ({Name}), {RuleCount} tag rule(s).", camera.CameraId, camera.Name, camera.EventTagRules.Count);
    }

    /// <summary>Starts (or stops, or replaces) this camera's vendor-plugin session — the node-side
    /// half of ICameraIntegrationProvider. Independent of ReconcileEvents above in exactly the way
    /// that reconcile is independent of ReconcileMotion: a camera can have an ONVIF event session, a
    /// plugin session, both, or neither, and the two feed the same detection pipeline without
    /// knowing about each other.
    ///
    /// Only Dahua/Amcrest exists today, so the dispatch below is a single case. It's written as a
    /// switch on the key rather than an if, because the whole point of the registry is that the next
    /// provider is a new arm here plus a new descriptor in Core — not a new concept.</summary>
    private void ReconcileIntegration(NodeConfigCameraDto camera, CancellationToken stoppingToken)
    {
        var key = camera.IntegrationKey;
        var hasIntegration = !string.IsNullOrEmpty(key)
            && !string.IsNullOrEmpty(camera.IntegrationBaseUri)
            && Uri.TryCreate(camera.IntegrationBaseUri, UriKind.Absolute, out _);

        if (!hasIntegration)
        {
            if (_activeIntegrations.TryRemove(camera.CameraId, out var stopped))
            {
                _logger.LogInformation("Camera {CameraId} ({Name}) no longer needs a vendor integration — stopping it.",
                    camera.CameraId, camera.Name);
                stopped.Cts.Cancel();
            }
            return;
        }

        if (_activeIntegrations.TryGetValue(camera.CameraId, out var existing))
        {
            // Both the plugin *and* the address it talks to have to match — the base URI follows the
            // camera's DeviceServiceUri, so switching a camera between HTTP and HTTPS (or moving it to
            // a different port) changes it while the key stays "dahua-cgi". Comparing the key alone
            // left the old session running against an address that no longer answers, reconnecting
            // forever; see CameraIntegrationRecorder's own doc comment.
            if (existing.IntegrationKey == key && existing.BaseUri == camera.IntegrationBaseUri) return;
            existing.Cts.Cancel();
            _activeIntegrations.TryRemove(camera.CameraId, out _);
            _logger.LogInformation("Vendor integration changed for camera {CameraId} ({Name}) — restarting it against {BaseUri}.",
                camera.CameraId, camera.Name, camera.IntegrationBaseUri);
        }

        var baseUri = new Uri(camera.IntegrationBaseUri!);
        var integrationCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var integrationLogger = loggerFactory.CreateLogger($"Integration[{camera.Name}]");

        DahuaCgiEventSession session;
        switch (key)
        {
            case DahuaCgiIntegrationProvider.ProviderKey:
                session = new DahuaCgiEventSession(baseUri, camera.Username, camera.Password, integrationLogger);
                break;
            default:
                // A key this node's build doesn't know (config from a newer server). Logged once per
                // reconcile rather than throwing — an unknown plugin must not stop a camera recording.
                _logger.LogWarning("Camera {CameraId} ({Name}) asks for unknown integration '{Key}' — ignoring. " +
                    "This node may be older than the server.", camera.CameraId, camera.Name, key);
                integrationCts.Dispose();
                return;
        }

        var capturedCameraId = camera.CameraId; // same staleness reasoning as ReconcileEvents' own capture
        session.DetectionSpanCompleted += (kind, result) => _pendingMotionSpans.Enqueue(
            new MotionSpanReportItem(capturedCameraId, null, result.StartUtc, result.EndUtc, result.PeakScore,
                EventTagRuleId: null, DetectionKind: kind));

        var runTask = session.RunAsync(integrationCts.Token);
        _activeIntegrations[camera.CameraId] = new CameraIntegrationRecorder(
            integrationCts, runTask, session, key!, camera.IntegrationBaseUri!);
        _logger.LogInformation("Started '{Key}' integration for camera {CameraId} ({Name}).", key, camera.CameraId, camera.Name);
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

    /// <summary>M18: starts/restarts/stops this camera's always-on Sub live session — independent of
    /// ReconcileMotion above even though both key off the Sub stream's existence, the same way
    /// ReconcileMotion/ReconcileEvents/ReconcileIntegration are all independent of one another. Only
    /// called at all when config.AdaptiveStreamingEnabled is true (see Reconcile's caller); the
    /// disabled case is handled entirely by Reconcile's own "stop every running Sub live session"
    /// loop above, not here — a per-camera reconcile has no way to notice "the global toggle just
    /// flipped off," only "this specific camera changed," so the blanket teardown has to live at the
    /// call site instead.
    ///
    /// Always-on rather than viewer-count-gated: see SubLiveSession's own doc comment for why. A
    /// camera with no enabled Sub stream simply gets no adaptive switching, same as it already gets
    /// no motion detection without one — nothing new to explain there.</summary>
    private void ReconcileLiveSub(NodeConfigCameraDto camera, CancellationToken stoppingToken)
    {
        var subStream = camera.Streams.FirstOrDefault(s => s.Role == "Sub");
        if (subStream is null)
        {
            if (_activeLiveSub.TryRemove(camera.CameraId, out var stopped))
            {
                _logger.LogInformation("Camera {CameraId} ({Name}) no longer has a Sub stream — stopping its live session.", camera.CameraId, camera.Name);
                stopped.Cts.Cancel();
            }
            return;
        }

        var subRtspUri = InjectCredentials(subStream.RtspUri, camera.Username, camera.Password);
        if (_activeLiveSub.TryGetValue(camera.CameraId, out var existing))
        {
            if (existing.StreamSignature == subRtspUri) return; // already running, unchanged
            existing.Cts.Cancel();
            _activeLiveSub.TryRemove(camera.CameraId, out _);
            _logger.LogInformation("Sub stream configuration changed for camera {CameraId} ({Name}) — restarting live session.", camera.CameraId, camera.Name);
        }

        var liveSubCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var liveSubLogger = loggerFactory.CreateLogger($"LiveSub[{camera.Name}]");
        var liveSubSession = new SubLiveSession(new SubLiveSessionOptions(ffmpegPath, subRtspUri), liveSubLogger);

        var liveSubRunTask = liveSubSession.RunAsync(liveSubCts.Token);
        _activeLiveSub[camera.CameraId] = new CameraLiveSubRecorder(liveSubCts, liveSubRunTask, liveSubSession, subRtspUri);
        _logger.LogInformation("Started Sub live session for camera {CameraId} ({Name}).", camera.CameraId, camera.Name);
    }

    // ── Privacy masking (M18) ────────────────────────────────────────────────────
    private static IReadOnlyList<string> BuildPrivacyMaskFilters(List<NodeConfigZoneDto> zones) =>
        PrivacyMaskFilterBuilder.BuildDrawboxFilters(
            zones.Where(z => z.Kind == nameof(ZoneKind.Privacy))
                .Select(z => ZoneRasterizer.ParsePolygon(z.PolygonJson)));

    /// <summary>Null when the camera has no enabled Privacy zone at all — RecordingSession never even
    /// looks at VideoEncoder in that case (see BuildCodecArgs), so there's nothing to choose. Also
    /// null when it does but this node's own capability probe hasn't reported an encoder yet — see
    /// the "masking with software libx264" fallback where this is called from.</summary>
    private string? ChoosePrivacyEncoder(List<NodeConfigZoneDto> zones) =>
        zones.Any(z => z.Kind == nameof(ZoneKind.Privacy)) ? EncoderSelection.ChooseH264Encoder(_detectedEncoders ?? []) : null;

    // Content-equality signature (same shape as BuildZoneConfigSignature) covering exactly what
    // RecordingSession's transcode branch actually reads: each enabled Privacy zone's own polygon,
    // plus the chosen encoder — either changing means the ffmpeg command line this camera's session
    // was started with is now stale and needs restarting to take effect. ServerMotion/Ignore zone
    // changes deliberately don't appear here; those only affect ReconcileMotion's own separate
    // session, not this one.
    private static string BuildPrivacyMaskSignature(List<NodeConfigZoneDto> zones, string? encoder) =>
        string.Join('|', zones.Where(z => z.Kind == nameof(ZoneKind.Privacy)).OrderBy(z => z.ZoneId)
            .Select(z => $"{z.ZoneId}:{z.PolygonJson}")) + "#" + encoder;

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
        var hasIntegration = _activeIntegrations.TryGetValue(pending.CameraId, out var integrationRecorder);
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
            // An object detection (person/vehicle/face) is one more signal in the same OR-chain: a
            // camera reporting it can see a person is reporting activity by any reasonable reading.
            // Being an OR term, this can only ever keep a segment that would otherwise have been
            // discarded — never the reverse — which also fixes a camera whose firmware emits object
            // topics but no motion ones, and which therefore discarded everything under Motion mode.
            // A vendor plugin is one more source in the same OR chain — and on hardware that only
            // reports objects through its own API (this fleet), it's the only one that ever sees a
            // person at all. Still an OR term, so it can only keep footage, never discard it.
            hasSignalSession = hasMotionSession || hasEventSession || hasIntegration;
            hadSignalInWindow =
                (hasMotionSession && motionRecorder!.Session.HasMotionSince(windowStart)) ||
                (hasEventSession && (
                    eventRecorder!.Session.HasMotionSince(windowStart) || eventRecorder.Session.IsMotionActive ||
                    eventRecorder.Session.AnyDrivingRuleHasMotionSince(windowStart) || eventRecorder.Session.AnyDrivingRuleActive ||
                    eventRecorder.Session.AnyDetectionSince(windowStart) || eventRecorder.Session.AnyDetectionActive)) ||
                (hasIntegration && (
                    integrationRecorder!.Session.AnyDetectionSince(windowStart) || integrationRecorder.Session.AnyDetectionActive));
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
