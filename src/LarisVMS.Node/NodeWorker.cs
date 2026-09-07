using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Logging;
using LarisVMS.Media;
using LarisVMS.Node.Update;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

namespace LarisVMS.Node;

public class NodeWorker(NodeApiClient api, string ffmpegPath, string fallbackStorageRoot, int livePort,
    NodeConfig registration, ILoggerFactory loggerFactory, OnvifEventsClient onvifEventsClient,
    UpdateService updateService, FileLoggerProvider fileLogger) : BackgroundService
{
    private readonly ILogger<NodeWorker> _logger = loggerFactory.CreateLogger<NodeWorker>();
    private string _appliedLogLevel = "Information";

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

    // Object detection plan decisions 2/3/7: NodeWorker holds no session object of its own for AI
    // detection (the whole pipeline lives in the sibling LarisVMS.Vision.Service process) — just
    // enough bookkeeping to avoid redundant start calls and to fold Vision Service's own reports
    // into the existing _pendingMotionSpans pipeline. Concurrent for the same reason _active is:
    // read from Program.cs's own new localhost-only POST /detections handler (a request thread),
    // written from the reconcile loop.
    private readonly ConcurrentDictionary<Guid, CameraVisionRecorder> _activeVision = new();
    private readonly ConcurrentDictionary<Guid, byte> _warnedVisionNoAccelerator = new();
    private readonly ConcurrentDictionary<Guid, byte> _warnedVisionMissingSubStream = new();
    private readonly VisionServiceSupervisor _visionSupervisor =
        new(AppContext.BaseDirectory, ffmpegPath, loggerFactory.CreateLogger<VisionServiceSupervisor>());
    // Fetches the ~320 MB onnxruntime_providers_cuda.dll from the server on demand — it isn't in the
    // node package (see CudaProviderProvisioner). Only does anything on an NVIDIA node with the CUDA
    // Toolkit installed.
    private readonly CudaProviderProvisioner _cudaProvider =
        new(AppContext.BaseDirectory, api, loggerFactory.CreateLogger<CudaProviderProvisioner>());
    // Explicit timeout rather than HttpClient's 100s default. Nothing on this loopback control channel
    // is long-running any more: /start used to block for the whole detection-engine build (minutes,
    // with TensorRT), and a 100s timeout there meant every slow start failed client-side, dropped its
    // _activeVision entry, and got re-sent on the next 30s reconcile — which tore down the in-progress
    // build and started another. The Vision Service now builds its engines off the request path, so a
    // call that takes even 15s here means something is genuinely wrong and should say so quickly.
    private readonly HttpClient _visionHttp = new()
    {
        BaseAddress = new Uri($"http://127.0.0.1:{VisionServiceSupervisor.Port}/"),
        Timeout = TimeSpan.FromSeconds(15),
    };

    // Cameras with a /start POST in flight right now. Without this, a start that outlives a reconcile
    // tick gets issued again by the next one (the _activeVision entry is removed on failure, and a
    // timeout is a failure), stacking concurrent starts for the same camera — each of which replaces
    // the pipeline the previous one was still setting up.
    private readonly ConcurrentDictionary<Guid, byte> _visionStartsInFlight = new();

    /// <summary>Same client this worker's own reconcile loop uses to start/stop watching a camera —
    /// exposed so DetectionOverlayHandler (decision 6's live-view box overlay) can poll
    /// GET /cameras/{id}/detections without opening a second HttpClient against the same base
    /// address.</summary>
    public HttpClient VisionHttpClient => _visionHttp;
    private IReadOnlyList<AiAccelerator> _detectedAccelerators = [];
    private AiAccelerator? _resolvedAccelerator;
    private DetectionModelFamily _resolvedDetectionModelFamily = DetectionModelFamily.DFine;
    // Node-wide (not per-camera) — set once the first time DetectionModelSelection.Choose actually
    // substitutes D-FINE for an unimplemented family, so the log isn't repeated every reconcile.
    private bool _warnedDetectionModelFallback;

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

    // Probed once at startup alongside the encoders (same "the installed ffmpeg build doesn't
    // change while this process runs" reasoning) — gates whether this node writes hover thumbnails
    // and segment-seek snapshot crops as WebP or falls back to JPEG. The Vision Service's eager
    // crops are always WebP (SkiaSharp, no ffmpeg). Defaults false so a node that hasn't finished
    // probing keeps producing JPEG until it has.
    private bool _webpSupported;

    /// <summary>Whether this node's ffmpeg can encode WebP (libwebp) — read by the thumbnail and
    /// snapshot-image routes to pick the cache file's format. See <see cref="_webpSupported"/>.</summary>
    public bool WebpSupported => _webpSupported;

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

    /// <summary>Object detection plan pass 3c-1: the currently-running MotionSession for a camera, if
    /// ServerMotion is active on it — null covers "not this node," "not currently recording," and
    /// "this camera's motion source isn't ServerMotion" identically, since MotionZoneOverlayHandler's
    /// only reaction to any of them is the same: report no scores for this tick.</summary>
    public MotionSession? TryGetMotionSession(Guid cameraId) => _activeMotion.TryGetValue(cameraId, out var r) ? r.Session : null;

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
    public async Task<byte[]?> CaptureThumbnailAsync(string filePath, int offsetSeconds, CancellationToken ct, int maxDimension = ThumbnailCapture.DefaultMaxDimension, int quality = ThumbnailCapture.DefaultQuality, bool webp = false)
    {
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        gateCts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await OnDemandThumbnailGate.WaitAsync(gateCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Logged as its own distinct case: a bare "capture returned null" at the call site is
            // indistinguishable from a genuine ffmpeg failure otherwise, which is exactly what made a
            // gate-contention burst (see OnDemandSnapshotGate's own doc comment for a confirmed live
            // case of this on the sibling gate) look like a data/geometry bug instead of a busy gate.
            _logger.LogWarning("On-demand thumbnail capture for {FilePath} at offset {OffsetSeconds}s gave up after {Timeout}s waiting for a free OnDemandThumbnailGate slot.", filePath, offsetSeconds, 3);
            return null; // gate stayed full for 3s straight — treat like any other capture failure
        }
        try
        {
            return await ThumbnailCapture.CaptureAsync(ffmpegPath, filePath, offsetSeconds, ct, maxDimension: maxDimension, quality: quality, webp: webp);
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
    public async Task<byte[]?> CaptureThumbnailInBackgroundAsync(string filePath, int offsetSeconds, CancellationToken ct, bool webp = false)
    {
        await BackgroundThumbnailGate.WaitAsync(ct);
        try
        {
            return await ThumbnailCapture.CaptureAsync(ffmpegPath, filePath, offsetSeconds, ct, lowPriority: true, webp: webp);
        }
        finally
        {
            BackgroundThumbnailGate.Release();
        }
    }

    // Object detection plan decision 10: its own gate, mirroring OnDemandThumbnailGate above —
    // separate from it (not shared) for the same reason the thumbnail on-demand/background gates are
    // kept apart: a burst of snapshot-image requests shouldn't be able to starve an ordinary hover
    // preview, or vice versa.
    //
    // Sized (and timed out) very differently from OnDemandThumbnailGate despite the "mirrors it"
    // framing above, because the two have genuinely different traffic shapes: a hover hits this gate
    // one request at a time as the viewer's mouse moves, where an old request really is stale by the
    // time a slot frees up (the 3s give-up is right there). The Snapshots page instead requests up to
    // a full page's worth of AI-detection cards (24) in one burst on load — every one of them still
    // wants its image, none are stale — and a single capture measured live at 3.5-4.4s. At the
    // original size(2)/3s, only the first couple of a 24-card burst could ever get a slot in time;
    // every other card 502'd — confirmed live, and it looked like a data/geometry bug (a fixed set of
    // spans always failing) purely because the same handful kept losing the race on every reload.
    private static readonly SemaphoreSlim OnDemandSnapshotGate = new(6, 6);
    private static readonly TimeSpan OnDemandSnapshotGateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Object detection plan decision 10: extracts one cropped JPEG frame from an
    /// already-recorded segment file for an AI-detection MotionSpan's best-frame image — same
    /// "no camera-assignment check" reasoning as CaptureThumbnailAsync (the /snapshot-image route's
    /// own directory-prefix validation already confirms filePath belongs to this node before this is
    /// ever called).</summary>
    public async Task<byte[]?> CaptureSnapshotImageAsync(string filePath, double offsetSeconds,
        double boxX, double boxY, double boxW, double boxH, int frameWidth, int frameHeight, CancellationToken ct,
        bool webp = false)
    {
        using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        gateCts.CancelAfter(OnDemandSnapshotGateTimeout);
        try
        {
            await OnDemandSnapshotGate.WaitAsync(gateCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // See CaptureThumbnailAsync's identical log for why this is worth distinguishing from a
            // genuine ffmpeg failure — this exact gate at its old size(2)/3s is the confirmed live
            // cause of what looked like a handful of AI-detection spans always failing to crop.
            _logger.LogWarning("On-demand snapshot-image capture for {FilePath} at offset {OffsetSeconds:0.###}s gave up after {Timeout}s waiting for a free OnDemandSnapshotGate slot.", filePath, offsetSeconds, OnDemandSnapshotGateTimeout.TotalSeconds);
            return null; // gate stayed full for the whole timeout — treat like any other capture failure
        }
        try
        {
            return await SnapshotImageCapture.CaptureAsync(ffmpegPath, filePath, offsetSeconds, boxX, boxY, boxW, boxH, frameWidth, frameHeight, ct, logger: _logger, webp: webp);
        }
        finally
        {
            OnDemandSnapshotGate.Release();
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

    /// <summary>The archive storage root the most recent reconcile resolved (config's
    /// ArchiveRootPath), or null when no archive volume is configured. The media endpoints accept a
    /// segment path under either this or <see cref="StorageRoot"/>; StorageManager moves aged-out
    /// footage here. Volatile for the same read-from-request-threads reason as StorageRoot.</summary>
    public volatile string? ArchiveRoot;

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
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
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

        _webpSupported = await FfmpegCapabilityProber.HasLibWebpAsync(ffmpegPath, stoppingToken);
        _logger.LogInformation("Cached thumbnail/snapshot image format: {Format}",
            _webpSupported ? "WebP (ffmpeg has libwebp)" : "JPEG (ffmpeg has no libwebp encoder)");

        // Object detection plan decision 2: probed once, same reasoning as encoders above — this
        // node's hardware doesn't change while the process is running. AccelSelection combines this
        // with the admin's own AiAccelerator choice (from config, resolved fresh every reconcile)
        // to decide which accelerator to actually use.
        _detectedAccelerators = await AccelCapabilityProber.ProbeAsync();
        _logger.LogInformation("Detected AI accelerator(s): {Accelerators}",
            _detectedAccelerators.Count > 0 ? string.Join(", ", _detectedAccelerators) : "(none detected)");

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
            // FIRST, before any of the winding-down below. This kills a child process that holds
            // open file handles (its own exe, onnxruntime and the CUDA/cuDNN natives beside it) and
            // a GPU context, and everything after it is best-effort network I/O that is allowed to
            // be slow: the flushes below run with CancellationToken.None and carry the API client's
            // own per-call timeout, so a server that's unreachable at shutdown can push this block
            // past the host's ShutdownTimeout. When that fires, whatever is left in this finally
            // never runs — and this used to be the very last line, so the one step that must not be
            // skipped was the first to be lost. Confirmed on a real node: `Stop-Service` returned
            // with LarisVMS.Vision.Service.exe still running, which then failed a same-path DLL copy
            // with "being used by another process". The job object in VisionServiceSupervisor is the
            // backstop for the paths no finally block can cover at all (crash, taskkill).
            _visionSupervisor.Stop();

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

            // No RunTask to await for AI detection — the whole pipeline lives in the sibling
            // process, not an in-process session object — so stopping it is just tearing down the
            // process itself. Vision Service's own shutdown (CameraDetectionPipeline.DisposeAsync)
            // flushes any in-progress spans and reports them back before it exits, the same
            // philosophy every other session type here already follows.
            _visionSupervisor.Stop();
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
                // Archive volume (SMB / USB) usage — only measured when an archive root is configured.
                var archiveUsage = ArchiveRoot is { } archiveRoot ? DiskSpace.TryGetUsage(archiveRoot) : null;
                // Primary volume over the watermark → footage is being archived/deleted early; surfaced
                // as a warning next to the node on Admin/Nodes.
                var storagePressure = usage is { TotalBytes: > 0 } u
                    && 100.0 * (u.TotalBytes - u.FreeBytes) / u.TotalBytes > config.WatermarkPercent;
                var heartbeat = await api.HeartbeatAsync(new NodeHeartbeatRequest(NodeVersion.Current, usage?.FreeBytes, usage?.TotalBytes, livePort,
                    DateTime.UtcNow, _detectedEncoders?.ToList(), _detectedAccelerators.Select(a => a.ToString()).ToList(),
                    archiveUsage?.FreeBytes, archiveUsage?.TotalBytes, storagePressure), ct);

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
        ArchiveRoot = string.IsNullOrWhiteSpace(config.ArchiveRootPath) ? null : config.ArchiveRootPath;
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

        foreach (var cameraId in _activeVision.Keys.Except(desired.Keys).ToList())
        {
            _logger.LogInformation("Camera {CameraId} no longer assigned to this node — stopping AI detection.", cameraId);
            if (_activeVision.TryRemove(cameraId, out _)) _ = StopVisionWatchAsync(cameraId);
        }

        // Object detection plan decisions 2/3: resolved once per reconcile (this node's own hardware
        // doesn't change; the admin's AiAccelerator choice can, so this is re-evaluated every cycle
        // the same way every other config-driven decision here is). Auto never falls back to Cpu —
        // see AccelSelection's own doc comment.
        var desiredAccelerator = Enum.TryParse<AiAccelerator>(config.AiAccelerator, ignoreCase: true, out var parsedAccel)
            ? parsedAccel : AiAccelerator.Auto;
        _resolvedAccelerator = AccelSelection.Choose(desiredAccelerator, _detectedAccelerators);

        // Same per-reconcile re-evaluation as the accelerator above. Node-scoped, not per-camera —
        // see NodeConfigResponse.DetectionModelFamily's own doc comment for why.
        var desiredModelFamily = Enum.TryParse<DetectionModelFamily>(config.DetectionModelFamily, ignoreCase: true, out var parsedFamily)
            ? parsedFamily : DetectionModelFamily.Auto;
        var accelForModelSelection = _resolvedAccelerator ?? AiAccelerator.Cpu;
        // Auto → YOLOX on every accelerator now; the only remaining fallback is RF-DETR (no decoder).
        var idealModelFamily = desiredModelFamily == DetectionModelFamily.Auto ? DetectionModelFamily.YoloX : desiredModelFamily;
        _resolvedDetectionModelFamily = DetectionModelSelection.Choose(desiredModelFamily, accelForModelSelection);
        if (idealModelFamily != _resolvedDetectionModelFamily && !_warnedDetectionModelFallback)
        {
            _warnedDetectionModelFallback = true;
            _logger.LogWarning(
                "Detection model family {Ideal} is not yet implemented — falling back to {Fallback} for every " +
                "camera on this node until it ships.", idealModelFamily, _resolvedDetectionModelFamily);
        }

        ApplyLogLevel(config.LogLevel);

        var anyCameraWantsAiDetection = config.Cameras.Any(c => c.AiDetectionEnabled);
        if (anyCameraWantsAiDetection && _resolvedAccelerator is not null)
        {
            // Tell the sibling process which ONNX Runtime backend to load for this machine — it
            // restarts itself if this changed since it started (VisionServiceSupervisor).
            _visionSupervisor.SetPreferredAccelerator(_resolvedAccelerator.Value.ToString());

            if (_resolvedAccelerator == AiAccelerator.Nvidia)
            {
                // Pull the big CUDA provider DLL from the server if this NVIDIA node doesn't have it
                // yet (fire-and-forget; the Vision Service resolves to DirectML until it lands).
                _ = _cudaProvider.EnsureAsync(stoppingToken);
                if (_cudaProvider.ConsumeJustProvisioned())
                {
                    _logger.LogInformation("CUDA provider library now present — restarting the Vision Service to switch it to CUDA.");
                    _visionSupervisor.Stop();
                }
            }

            _visionSupervisor.EnsureRunning();
            PruneStaleVisionWatches();
        }
        else
        {
            if (_visionSupervisor.IsRunning) _logger.LogInformation("Stopping LarisVMS.Vision.Service — no longer needed.");
            _visionSupervisor.Stop();
            // The sibling process is gone, so every camera it was watching needs a fresh /start
            // call once it (or a usable accelerator) comes back — nothing to explicitly stop
            // per-camera here, the process teardown already did that.
            _activeVision.Clear();
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
            ReconcileVision(camera, config);
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
        // Object detection plan decision 9: MotionSpanCompleted (plain motion) and DetectionSpanCompleted
        // (the camera's own onboard object classifier) are both the CameraEvent source — same PullPoint
        // channel, same session — so both gate together, restricted to whichever of the three generic
        // sources is the camera's chosen primary in Motion mode. RuleSpanCompleted (a named EventTagRule)
        // stays unconditional — it always reports regardless of the chosen primary.
        eventSession.MotionSpanCompleted += result =>
        {
            if (ShouldReportGenericMotion(capturedCameraId, MotionDetectionSource.CameraEvent))
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(capturedCameraId, null, result.StartUtc, result.EndUtc, result.PeakScore));
        };
        eventSession.RuleSpanCompleted += (ruleId, result) => _pendingMotionSpans.Enqueue(
            new MotionSpanReportItem(capturedCameraId, null, result.StartUtc, result.EndUtc, result.PeakScore, ruleId));
        eventSession.DetectionSpanCompleted += (kind, result) =>
        {
            if (ShouldReportGenericMotion(capturedCameraId, MotionDetectionSource.CameraEvent))
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(capturedCameraId, null, result.StartUtc, result.EndUtc, result.PeakScore,
                    EventTagRuleId: null, DetectionKind: kind));
        };

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
        // Object detection plan decision 9: this is the Integration source, restricted to whichever
        // of the three generic sources is the camera's chosen primary in Motion mode.
        session.DetectionSpanCompleted += (kind, result) =>
        {
            if (ShouldReportGenericMotion(capturedCameraId, MotionDetectionSource.Integration))
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(capturedCameraId, null, result.StartUtc, result.EndUtc, result.PeakScore,
                    EventTagRuleId: null, DetectionKind: kind));
        };

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
    // Detection/hardware-acceleration overhaul pass 3c-2: the sentinel MotionZoneMask.ZoneId used
    // for Grid mode's single aggregate region (the whole frame minus masked cells) — never a real
    // Zone row's id, and never reported as-is: the MotionSpanCompleted subscriber below always
    // rewrites it to null before enqueueing, matching a camera-pushed span's own "no zone" shape
    // (already a supported upsert key per NodeService.RecordMotionSpansAsync).
    private static readonly Guid GridRegionZoneId = Guid.Empty;

    private void ReconcileMotion(NodeConfigCameraDto camera, CancellationToken stoppingToken)
    {
        var isGridMode = string.Equals(camera.MotionRegionMode, nameof(MotionRegionMode.Grid), StringComparison.OrdinalIgnoreCase);
        var serverMotionZones = camera.Zones.Where(z => z.Kind == nameof(ZoneKind.ServerMotion)).ToList();
        // The first real consumer of Sub in this codebase — Main is recording-only and Live's
        // auto-switch-to-Sub (plan §M5) was never actually implemented, so this path is genuinely
        // new ground, flagged as such in MotionSession's own doc comment.
        var subStream = camera.Streams.FirstOrDefault(s => s.Role == "Sub");

        // Grid mode's own "is there anything to watch" check (at least one unmasked cell) replaces
        // Polygon mode's "at least one enabled ServerMotion zone" — reusing ShouldRunServerMotion's
        // existing int>0 gate rather than giving it a second parameter, since either answer is really
        // just "yes, this region isn't empty."
        var hasAnythingToWatch = isGridMode
            ? MotionGrid.HasAnyUnmaskedCell(camera.MotionGridMask, camera.MotionGridSize)
            : serverMotionZones.Count > 0;

        if (!ShouldRunServerMotion(camera.ServerMotionEnabled, hasAnythingToWatch ? 1 : 0, subStream is not null))
        {
            if (_activeMotion.TryRemove(camera.CameraId, out var stopped))
            {
                _logger.LogInformation(
                    camera.ServerMotionEnabled
                        ? "Camera {CameraId} ({Name}) has nothing to watch in {Mode} mode (with a Sub stream) — stopping motion session."
                        : "Camera {CameraId} ({Name}) has ServerMotion disabled — stopping motion session.",
                    camera.CameraId, camera.Name, camera.MotionRegionMode);
                stopped.Cts.Cancel();
            }
            return;
        }

        // Pass 0 of the detection/hardware-acceleration overhaul: MotionSession's own decode is now
        // hwaccel-aware (see MotionSessionOptions.HardwareAcceleration's doc comment for why this was
        // the single largest CPU cost in the whole detection stack). _resolvedAccelerator is
        // re-evaluated every reconcile, same as ReconcileVision's own use of it, so it belongs in the
        // restart signature below — otherwise a node that only just detected/gained a usable
        // accelerator would keep an already-running session on software decode until it happened to
        // restart for an unrelated reason (the exact class of stale-config bug this codebase was
        // already caught by once, in v0.29.0).
        var motionHwaccel = _resolvedAccelerator is { } accel ? AccelToFfmpegHwaccel(accel) : null;

        // Pass 3c-2: only whichever method is actually active belongs in the restart signature —
        // Zone rows changing while Grid mode is active (or vice versa) must not restart a session
        // that isn't using them, but the mode itself, and the grid's own size/mask/sensitivity while
        // active, must (per the plan's own "mode and mask must be in the session restart signature").
        var regionSignature = isGridMode
            ? $"Grid:{camera.MotionGridSize}:{camera.MotionGridMask}:{camera.MotionGridSensitivity}"
            : $"Polygon:{BuildZoneConfigSignature(camera.Zones)}";
        var signature = regionSignature + "|" + motionHwaccel;
        if (_activeMotion.TryGetValue(camera.CameraId, out var existing))
        {
            if (existing.ZoneConfigSignature == signature) return; // unchanged — leave it running
            existing.Cts.Cancel();
            _activeMotion.TryRemove(camera.CameraId, out _);
            _logger.LogInformation("Motion region configuration changed for camera {CameraId} ({Name}) — restarting motion session.", camera.CameraId, camera.Name);
        }

        // ShouldRunServerMotion's hasSubStream argument is subStream is not null, and it already
        // returned true above (or this method returned already) — the compiler can't carry that
        // correlation through a pure helper call the way it does a direct `is null` check, so this
        // is a real invariant, not an unchecked assumption.
        var subRtspUri = InjectCredentials(subStream!.RtspUri, camera.Username, camera.Password);
        var motionOptions = new MotionSessionOptions(ffmpegPath, subRtspUri, HardwareAcceleration: motionHwaccel);

        List<MotionZoneMask> zoneMasks;
        int? gridSizeForSession = null;
        if (isGridMode)
        {
            // Grid mode replaces Polygon/Ignore entirely as far as MotionSession is concerned — a
            // single region (the whole frame minus masked cells) flowing through the exact same
            // MotionZoneMask/MotionHysteresis mechanism a lone ServerMotion zone would. Existing Zone
            // rows on this camera are left untouched (per the plan's own "never delete the inactive
            // method's configuration") but simply don't feed MotionSession while Grid mode is active.
            var gridMask = MotionGrid.Rasterize(camera.MotionGridMask, camera.MotionGridSize, motionOptions.Width, motionOptions.Height);
            zoneMasks = [new MotionZoneMask(GridRegionZoneId, gridMask, camera.MotionGridSensitivity)];
            gridSizeForSession = camera.MotionGridSize;
        }
        else
        {
            // Ignore zones are combined once into a single exclusion mask (their union), then
            // subtracted from every ServerMotion zone's own mask — a pixel inside any Ignore zone
            // never counts toward motion for any zone on this camera, not just one it happens to
            // overlap most.
            bool[]? ignoreMask = null;
            foreach (var z in camera.Zones.Where(z => z.Kind == nameof(ZoneKind.Ignore)))
            {
                var rasterized = ZoneRasterizer.Rasterize(ZoneRasterizer.ParsePolygon(z.PolygonJson), motionOptions.Width, motionOptions.Height);
                ignoreMask = ignoreMask is null ? rasterized : ZoneRasterizer.Union(ignoreMask, rasterized);
            }

            zoneMasks = serverMotionZones.Select(z =>
            {
                var raw = ZoneRasterizer.Rasterize(ZoneRasterizer.ParsePolygon(z.PolygonJson), motionOptions.Width, motionOptions.Height);
                var mask = ignoreMask is null ? raw : ZoneRasterizer.Subtract(raw, ignoreMask);
                return new MotionZoneMask(z.ZoneId, mask, z.Sensitivity);
            }).ToList();
        }

        var motionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var motionLogger = loggerFactory.CreateLogger($"Motion[{camera.Name}]");
        var motionSession = new MotionSession(motionOptions, zoneMasks, motionLogger, gridSizeForSession);
        // Object detection plan decision 9: this is ServerMotion, one of the three generic
        // "something moved" sources restricted to whichever one is the camera's chosen primary in
        // Motion mode — see ShouldReportGenericMotion's own doc comment. Grid mode's own span always
        // reports ZoneId = null — GridRegionZoneId is an internal sentinel only, never a real zone.
        motionSession.MotionSpanCompleted += (zoneId, result) =>
        {
            if (ShouldReportGenericMotion(camera.CameraId, MotionDetectionSource.ServerMotion))
                _pendingMotionSpans.Enqueue(new MotionSpanReportItem(camera.CameraId, isGridMode ? null : zoneId, result.StartUtc, result.EndUtc, result.PeakScore));
        };

        var motionRunTask = motionSession.RunAsync(motionCts.Token);
        _activeMotion[camera.CameraId] = new CameraMotionRecorder(motionCts, motionRunTask, motionSession, signature);
        _logger.LogInformation("Started motion detection for camera {CameraId} ({Name}), mode {Mode}.", camera.CameraId, camera.Name, camera.MotionRegionMode);
    }

    /// <summary>Detection/hardware-acceleration overhaul, pass 0 — see Camera.ServerMotionEnabled's
    /// own doc comment for why this toggle exists alongside the pre-existing "is there anything to
    /// watch" check. Pure and unit-tested directly, same reasoning as ShouldDiscardSegment above.</summary>
    internal static bool ShouldRunServerMotion(bool serverMotionEnabled, int serverMotionZoneCount, bool hasSubStream) =>
        serverMotionEnabled && serverMotionZoneCount > 0 && hasSubStream;

    // Content-equality signature, not a hash — the strings involved are small (a handful of zones
    // per camera at most) and readable in a debugger, so there's no reason to hash away that.
    private static string BuildZoneConfigSignature(List<NodeConfigZoneDto> zones) =>
        string.Join('|', zones.OrderBy(z => z.ZoneId).Select(z => $"{z.ZoneId}:{z.Kind}:{z.Sensitivity}:{z.PolygonJson}"));

    /// <summary>Applies the deployment-wide Logging.Level to this node's own file logger and, if the
    /// Vision Service is up, pushes it there too — both take effect without a restart. Called every
    /// reconcile; only acts (and logs) when the resolved level actually changed.</summary>
    private void ApplyLogLevel(string level)
    {
        if (string.Equals(level, _appliedLogLevel, StringComparison.OrdinalIgnoreCase)) return;
        _appliedLogLevel = level;

        var parsed = LogLevels.Parse(level);
        fileLogger.MinLevel = parsed;
        _visionSupervisor.SetLogLevel(level);
        _logger.LogInformation("Log level set to {Level} (from the deployment-wide Logging.Level setting).", parsed);
    }

    /// <summary>Starts/replaces/stops this camera's AI detection watch via LarisVMS.Vision.Service's
    /// own control API (object detection plan decisions 2/3/7) — independent of
    /// ReconcileMotion/ReconcileEvents/ReconcileIntegration above, the same way they're independent
    /// of each other: a camera can have AI detection enabled with or without any of the others
    /// configured. No-op entirely if AI detection isn't enabled for this camera, this node has no
    /// usable accelerator resolved, or the Vision Service process isn't currently running —
    /// Reconcile's own EnsureRunning/Stop call, made once per cycle before this per-camera loop
    /// runs, already logs the "why" for the process-level cases; this only warns for the
    /// per-camera-specific ones (no accelerator, no stream matching the configured role).</summary>
    private void ReconcileVision(NodeConfigCameraDto camera, NodeConfigResponse config)
    {
        if (!camera.AiDetectionEnabled || _resolvedAccelerator is null || !_visionSupervisor.IsRunning)
        {
            if (_activeVision.TryRemove(camera.CameraId, out _)) _ = StopVisionWatchAsync(camera.CameraId);

            if (camera.AiDetectionEnabled && _resolvedAccelerator is null && _warnedVisionNoAccelerator.TryAdd(camera.CameraId, 0))
            {
                _logger.LogWarning(
                    "Camera {CameraId} ({Name}) has AI detection enabled but this node has no usable " +
                    "accelerator resolved — every other detection path already configured for it is unaffected.",
                    camera.CameraId, camera.Name);
            }
            return;
        }

        // Which stream feeds detection is itself configurable now (AiDetection.StreamRole, global +
        // per-camera override) — used to be unconditionally "Sub". Falls back to "Sub" for any
        // unrecognized value, matching the behavior from before this was configurable at all.
        var watchRole = camera.AiDetectionStreamRole == "Main" ? "Main" : "Sub";
        var watchStream = camera.Streams.FirstOrDefault(s => s.Role == watchRole);
        if (watchStream is null)
        {
            if (_warnedVisionMissingSubStream.TryAdd(camera.CameraId, 0))
            {
                _logger.LogWarning("Camera {CameraId} ({Name}) has AI detection enabled but no {Role} stream to watch it on.",
                    camera.CameraId, camera.Name, watchRole);
            }
            return;
        }

        var watchRtspUri = InjectCredentials(watchStream.RtspUri, camera.Username, camera.Password);

        // The Main stream — used as the watch-stream dimension fallback just below. Null until
        // RecordingSession has actually probed it.
        var mainStream = camera.Streams.FirstOrDefault(s => s.Role == "Main");

        // Pass 1 of the detection/hardware-acceleration overhaul: this camera's own real stream
        // dimensions, replacing the old global config.AiDetectionWidth/Height every camera used to
        // share regardless of its own aspect ratio. InferenceProfile only uses these as an aspect
        // ratio. The watch (Sub) stream is never ffmpeg-probed on its own — nothing on the node
        // decodes it unscaled (MotionSession/SubLiveSession force a fixed scale; VisionSession runs
        // in the sibling process). So fall back to the Main stream's real probed dimensions rather
        // than a hard-coded 1280x720 landscape: a portrait/corridor-mounted camera has a portrait
        // Main stream too, so this gets the aspect right for the real-world failure case. Only a
        // brand-new camera, before Main itself has been probed, still lands on 1280x720 — and the
        // signature below carries these values, so the watch restarts with the correct aspect the
        // moment Main is probed.
        //
        // The orientation override then corrects a camera that misreports the pair outright (ONVIF
        // advertising 704x480 for a stream it actually delivers as 480x704). Applied here, before
        // InferenceProfile sees it, so ffmpeg's own scale/pad chain is built for the real shape and
        // the model never receives a squashed frame — see DetectionOrientation for why this is an
        // operator setting rather than a measurement reported back by the Vision Service.
        var (sourceWidth, sourceHeight) = DetectionOrientation.Apply(
            camera.AiDetectionOrientation,
            watchStream.Width ?? mainStream?.Width ?? 1280,
            watchStream.Height ?? mainStream?.Height ?? 720);

        // Detection frame-rate cap: only apply an fps= filter when the probed Sub rate is genuinely
        // above the ceiling — an unknown or already-low rate is left alone so ffmpeg never duplicates
        // frames up to the target (which would *add* inference work).
        var decodeFpsCap = config.MaxDetectionFps > 0 && watchStream.Fps is { } fps && fps > config.MaxDetectionFps
            ? config.MaxDetectionFps
            : 0;

        var signature = string.Join('|', watchRtspUri, sourceWidth, sourceHeight, config.AspectMode,
            camera.AiConfidence, camera.AiIou, config.ReportIdleDetections, config.AiIdleTimeoutSeconds,
            watchRole, _resolvedAccelerator, _resolvedDetectionModelFamily, config.DFineWeights, config.YoloXSize,
            decodeFpsCap, config.GpuPreprocessing, config.DFineTensorRtMode, config.SnapshotMotionAccuracy);

        if (_activeVision.TryGetValue(camera.CameraId, out var existing) && existing.ConfigSignature == signature) return; // already watching, unchanged

        var request = new VisionStartCameraRequest(
            camera.CameraId, watchRtspUri, sourceWidth, sourceHeight,
            AccelToFfmpegHwaccel(_resolvedAccelerator.Value), camera.AiConfidence, camera.AiIou,
            config.ReportIdleDetections, config.AiIdleTimeoutSeconds,
            _resolvedDetectionModelFamily.ToString(), config.DFineWeights, $"http://127.0.0.1:{livePort}",
            config.AspectMode, config.GpuPreprocessing, config.YoloXSize, decodeFpsCap,
            // Log-readability only, and deliberately absent from `signature` above — renaming a
            // camera must not restart its pipeline (that would mean a multi-minute TensorRT engine
            // rebuild for a cosmetic change). The name in the Vision log therefore updates on the
            // next restart for some other reason, which is the right trade.
            camera.Name ?? "",
            // Detection.DFineTensorRtMode — node-scoped, only acted on for a D-FINE pipeline. In
            // `signature` above: switching it changes both the loaded model file and the compiled
            // TensorRT engine, so the pipeline must restart.
            config.DFineTensorRtMode,
            // Detection.SnapshotMotionAccuracy — global. In `signature` above: it changes how the
            // movement classifier and span lifecycle behave, so a change restarts the pipeline.
            config.SnapshotMotionAccuracy);

        // Never stack two starts for the same camera — see _visionStartsInFlight's own comment.
        if (!_visionStartsInFlight.TryAdd(camera.CameraId, 0)) return;

        _activeVision[camera.CameraId] = new CameraVisionRecorder(signature);
        _ = StartVisionWatchAsync(request);
        _logger.LogInformation("Starting AI detection watch for camera {CameraId} ({Name}).", camera.CameraId, camera.Name);
    }

    /// <summary>Maps a resolved AiAccelerator to VisionSession's own -hwaccel value. Only Nvidia
    /// maps to a real value: CUDA's scale_cuda GPU-hybrid decode is the only pairing actually
    /// verified end to end (in aitest, against real cameras) — see VisionSession.StartFfmpeg's own
    /// doc comment. Intel/AMD still get GPU-accelerated *inference* (whichever execution provider
    /// this Vision Service build was compiled for), just CPU decode until an equivalent verified
    /// hwaccel pairing exists for them.</summary>
    private static string? AccelToFfmpegHwaccel(AiAccelerator accelerator) => accelerator switch
    {
        AiAccelerator.Nvidia => "cuda",
        _ => null
    };

    private async Task StartVisionWatchAsync(VisionStartCameraRequest request)
    {
        try
        {
            var response = await _visionHttp.PostAsJsonAsync($"/cameras/{request.CameraId}/start", request);
            if (!response.IsSuccessStatusCode)
            {
                // The status code alone says nothing actionable — Vision Service puts the actual
                // reason (a missing CUDA runtime DLL, an absent model file, ...) in the response
                // body precisely so it lands here rather than only in that process's own logging.
                // Truncated because this repeats every reconcile tick for as long as the underlying
                // problem lasts, and best-effort because a body we can't read must never turn a
                // logged warning into a thrown exception.
                var detail = await ReadVisionErrorDetailAsync(response);
                _logger.LogWarning("Failed to start AI detection for camera {CameraId}: Vision Service returned {Status}{Detail}",
                    request.CameraId, response.StatusCode, detail is null ? "." : $" — {detail}");
                _activeVision.TryRemove(request.CameraId, out _); // retry on the next reconcile tick
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reach LarisVMS.Vision.Service to start watching camera {CameraId} — will retry next reconcile.", request.CameraId);
            _activeVision.TryRemove(request.CameraId, out _);
        }
        finally
        {
            _visionStartsInFlight.TryRemove(request.CameraId, out _);
        }
    }

    /// <summary>
    /// Drops any <see cref="_activeVision"/> entry the Vision Service isn't actually watching, so a
    /// later reconcile re-issues its start. Covers two silent drifts: the sibling process crashed and
    /// <see cref="VisionServiceSupervisor.EnsureRunning"/> brought it back within this same tick (so
    /// the <c>IsRunning</c> check in ReconcileVision never sees it down, and the stale signatures
    /// suppress every restart), and — now that the detection engine is built after /start has already
    /// returned 200 — a pipeline whose engine build failed after the fact.
    ///
    /// Fire-and-forget, like every other Vision Service call from this loop, so its effect lands on a
    /// subsequent tick rather than this one; these are drifts that have already persisted for at least
    /// a reconcile interval, so one more costs nothing. A service that doesn't answer (still starting,
    /// mid-restart) leaves this node's view untouched rather than tearing down watches that are fine.
    /// </summary>
    private void PruneStaleVisionWatches()
    {
        if (_activeVision.IsEmpty) return;
        _ = PruneStaleVisionWatchesAsync();
    }

    private async Task PruneStaleVisionWatchesAsync()
    {
        // Snapshot before the request as well as checking after it: a camera whose /start completes
        // while this call is in flight is absent from the response through no fault of its own, and
        // dropping it here would restart a pipeline that had only just finished being built.
        var inFlightBefore = _visionStartsInFlight.Keys.ToHashSet();

        List<Guid>? watched;
        try
        {
            watched = await _visionHttp.GetFromJsonAsync<List<Guid>>("/cameras");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the Vision Service's watched-camera list — leaving this node's view unchanged.");
            return;
        }
        if (watched is null) return;

        foreach (var cameraId in _activeVision.Keys)
        {
            if (watched.Contains(cameraId)) continue;
            if (inFlightBefore.Contains(cameraId) || _visionStartsInFlight.ContainsKey(cameraId)) continue;

            if (_activeVision.TryRemove(cameraId, out _))
            {
                _logger.LogInformation(
                    "LarisVMS.Vision.Service is not watching camera {CameraId} despite this node believing it is — " +
                    "re-starting the watch on the next reconcile. Check the vision log for an engine build failure.",
                    cameraId);
            }
        }
    }

    /// <summary>The human-readable reason out of a failed Vision Service /start response, or null if
    /// there isn't one to be had. Vision Service replies with ProblemDetails (Results.Problem), whose
    /// "detail" member carries the flattened exception message chain; anything else — a plain-text
    /// body, an unparseable one, an empty one — falls back to the raw text so a response shape this
    /// doesn't anticipate still surfaces *something* rather than being silently dropped. Never
    /// throws: this only exists to enrich a log line that's already being written.</summary>
    private static async Task<string?> ReadVisionErrorDetailAsync(HttpResponseMessage response)
    {
        const int maxLength = 500;
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return null;

            string text = body;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("detail", out var detail)
                    && detail.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(detail.GetString()))
                {
                    text = detail.GetString()!;
                }
            }
            catch (JsonException) { /* not JSON — fall through to the raw body below */ }

            text = text.Trim();
            return text.Length > maxLength ? text[..maxLength] + "…" : text;
        }
        catch
        {
            return null;
        }
    }

    private async Task StopVisionWatchAsync(Guid cameraId)
    {
        try
        {
            await _visionHttp.PostAsync($"/cameras/{cameraId}/stop", null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to tell LarisVMS.Vision.Service to stop watching camera {CameraId} (it may already be stopped, or the process itself may be gone).", cameraId);
        }
    }

    /// <summary>Called from Program.cs's new localhost-only POST /detections handler — folds a
    /// closed/checkpointed AI-detection span from LarisVMS.Vision.Service straight into the
    /// existing _pendingMotionSpans/FlushMotionSpansAsync pipeline, unchanged, the same way every
    /// other detection source already does.</summary>
    public void ReportVisionDetection(VisionDetectionReportItem item)
    {
        _pendingMotionSpans.Enqueue(new MotionSpanReportItem(
            item.CameraId, null, item.StartUtc, item.EndUtc, item.Score,
            EventTagRuleId: null, DetectionKind: null,
            DetectedObjectCategory: item.DetectedObjectCategory, DetectedObjectLabel: item.DetectedObjectLabel,
            BestFrameAtUtc: item.BestFrameAtUtc, BestBoxX: item.BestBoxX, BestBoxY: item.BestBoxY,
            BestBoxW: item.BestBoxW, BestBoxH: item.BestBoxH, BestBoxConfidence: item.BestBoxConfidence,
            MovingCount: item.MovingCount));

        if (_activeVision.TryGetValue(item.CameraId, out var recorder)) recorder.RecordDetection(item.EndUtc);
    }

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

    /// <summary>Object detection plan decision 9: resolves which of ServerMotion/CameraEvent/
    /// Integration is a Motion-mode camera's current "primary" generic motion-detection source — the
    /// admin's own explicit Camera.MotionDetectionSource choice when set, or (a camera created before
    /// this setting existed, or whose admin hasn't visited camera setup since) the richest signal
    /// actually configured for it, in priority order Integration &gt; CameraEvent &gt; ServerMotion —
    /// proposed default per the plan's own open-questions section. Pure and unit-tested directly,
    /// same reasoning as ShouldDiscardSegment above — a dynamic fallback computed fresh every time
    /// from whatever's actually running right now (so removing a camera's last configured
    /// integration, say, falls through to CameraEvent on the very next reconcile), not a one-time
    /// data migration/backfill baked in once at upgrade time.</summary>
    internal static MotionDetectionSource ResolvePrimaryMotionSource(string? explicitChoice, bool hasIntegration, bool hasEventSession)
    {
        if (Enum.TryParse<MotionDetectionSource>(explicitChoice, ignoreCase: true, out var chosen)) return chosen;
        if (hasIntegration) return MotionDetectionSource.Integration;
        if (hasEventSession) return MotionDetectionSource.CameraEvent;
        return MotionDetectionSource.ServerMotion;
    }

    /// <summary>Object detection plan decision 9: true if a generic "something moved" span from
    /// `source` (ServerMotion/CameraEvent/Integration) should actually be reported to the timeline
    /// for `cameraId` right now. Always true outside Motion mode — every configured source already
    /// tags the timeline freely there today, unchanged. In Motion mode, true only for whichever
    /// source ResolvePrimaryMotionSource resolves as this camera's current primary — the other two
    /// generic sources' own plain-motion spans are suppressed so the timeline doesn't collect
    /// redundant overlapping "something moved" entries for the same real event. Looked up fresh from
    /// _latestCameraConfig every time a candidate span closes, not captured at session-creation time
    /// — the exact staleness bug HandleSegmentCompleted's own doc comment already documents for
    /// RecordingMode, which would otherwise apply here too. Never called for EventTagRule
    /// (RuleSpanCompleted) or AI-detection reporting — both always report regardless of the chosen
    /// primary, see MotionDetectionSource's own doc comment.</summary>
    private bool ShouldReportGenericMotion(Guid cameraId, MotionDetectionSource source)
    {
        if (!_latestCameraConfig.TryGetValue(cameraId, out var camera)) return true; // fail open, same as HandleSegmentCompleted's own "no cached config" fallback
        if (!Enum.TryParse<RecordingMode>(camera.RecordingMode, ignoreCase: true, out var mode) || mode != RecordingMode.Motion) return true;
        return ResolvePrimaryMotionSource(camera.MotionDetectionSource, _activeIntegrations.ContainsKey(cameraId), _activeEvents.ContainsKey(cameraId)) == source;
    }

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
        var hasIntegration = _activeIntegrations.ContainsKey(cameraId);
        var hasVision = _activeVision.ContainsKey(cameraId);
        // M8 pass 6: any signal source counts for Motion — a camera relying only on its own onboard
        // detection (no ServerMotion zone drawn at all), only a vendor integration, or only AI
        // detection must still gate correctly, not just one with a server-side zone (object detection
        // plan decision 9 adds hasIntegration/hasVision here, matching DecideGatedSegment's own
        // broader check below, which already counted both). Event mode is narrower: only a *driving*
        // EventTagRule counts, since Event mode's whole point is "record only for this specific
        // tagged trigger," not "any activity" — an event session with no DrivesRecording=true rule
        // contributes nothing, same as having no session at all. See DecideGatedSegment for the
        // matching hadSignalInWindow check per mode.
        var hasSignalSource = mode == RecordingMode.Event
            ? hasEventSession && camera.EventTagRules.Any(r => r.DrivesRecording)
            : hasMotionSession || hasEventSession || hasIntegration || hasVision;

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
            var hasVision = _activeVision.TryGetValue(pending.CameraId, out var visionRecorder);

            // Object detection plan decision 9: exactly one of the three generic "something moved"
            // sources (ServerMotion/CameraEvent/Integration) counts toward hadSignalInWindow — whichever
            // ResolvePrimaryMotionSource resolves as this camera's current primary — rather than every
            // configured source counting (the pre-decision-9 OR-of-all-three behavior), so three
            // low-information "something moved" signals firing on the same real event don't each
            // independently keep/tag it. EventTagRule (AnyDrivingRuleHasMotionSince/AnyDrivingRuleActive,
            // M8 pass 8) and AI detection (AnyDetectionSince/AnyDetectionActive on the vision recorder)
            // are deliberately OUTSIDE that restriction and stay always-on OR terms regardless of the
            // chosen primary — a specific named tag, or an AI-confirmed moving object, can each still
            // keep a segment the primary source alone wouldn't have. Being OR terms throughout, none of
            // this can ever *discard* footage that would otherwise have been kept — only the three
            // generic sources' own relative weight changed.
            var explicitChoice = _latestCameraConfig.TryGetValue(pending.CameraId, out var freshCamera) ? freshCamera.MotionDetectionSource : null;
            var primarySource = ResolvePrimaryMotionSource(explicitChoice, hasIntegration, hasEventSession);

            hasSignalSession = hasMotionSession || hasEventSession || hasIntegration || hasVision;
            hadSignalInWindow =
                (primarySource == MotionDetectionSource.ServerMotion && hasMotionSession && motionRecorder!.Session.HasMotionSince(windowStart)) ||
                // CameraEvent's own plain-motion/onboard-classifier signal — see
                // CameraEventSession.IsMotionActive's doc comment for why HasMotionSince alone isn't
                // enough: many ONVIF implementations send exactly one notification per edge (rising,
                // then nothing again until falling), so LastMotionAtUtc goes stale relative to
                // windowStart for a segment decided well into a long, sparsely-reported event even
                // though motion never actually stopped. IsMotionActive has no timeout of its own — it's
                // only false once an actual falling-edge notification closes the span.
                (primarySource == MotionDetectionSource.CameraEvent && hasEventSession && (
                    eventRecorder!.Session.HasMotionSince(windowStart) || eventRecorder.Session.IsMotionActive ||
                    eventRecorder.Session.AnyDetectionSince(windowStart) || eventRecorder.Session.AnyDetectionActive)) ||
                (primarySource == MotionDetectionSource.Integration && hasIntegration && (
                    integrationRecorder!.Session.AnyDetectionSince(windowStart) || integrationRecorder.Session.AnyDetectionActive)) ||
                // EventTagRule — always on, independent of the chosen primary.
                (hasEventSession && (eventRecorder!.Session.AnyDrivingRuleHasMotionSince(windowStart) || eventRecorder.Session.AnyDrivingRuleActive)) ||
                // AI detection — always on, independent of the chosen primary. AnyDetectionSince/
                // AnyDetectionActive reflect every reported track (Moving is the only kind reported by
                // default; an Idle-only report can only appear here when ReportIdleDetections is
                // enabled for testing, a narrow, admin-opted-in edge case not worth a dedicated
                // MovementState wire field just to exclude it from this OR term).
                (hasVision && (visionRecorder!.AnyDetectionSince(windowStart) || visionRecorder.AnyDetectionActive));
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
