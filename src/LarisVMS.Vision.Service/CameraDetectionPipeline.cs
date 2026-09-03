using System.Collections.Concurrent;
using System.Net.Http.Json;
using SkiaSharp;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Media;
using LarisVMS.Vision.Capture;
using LarisVMS.Vision.Detection;
using LarisVMS.Vision.Inference;
using LarisVMS.Vision.Tracking;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Service;

/// <summary>
/// Owns one camera's whole detection pipeline: capture (VisionSession, decision 1) → inference
/// (IDetectionEngine — DFineEngine today, see DetectionEngineFactory) → tracking (ByteTracker) →
/// movement classification + best-frame tracking (MovementClassifier, decisions 7/10) → per-label
/// debouncing (MotionHysteresis, the same class DahuaCgiEventSession already uses, decision 7's own
/// "same shape" call-out) → reporting back to Node (decision 3).
///
/// One IDetectionEngine per camera, not shared across cameras watched by the same process — same
/// "not thread-safe, one instance per camera pipeline" ownership model the deleted YoloEngine
/// already established (DFineEngine's own InferenceSession isn't safe for concurrent Run calls
/// either), even though every camera loads the same underlying .onnx file independently.
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
    private readonly InferenceProfile _profile;
    private readonly VisionSession _session;
    private readonly LatestFrameSlot _slot;
    private readonly IDetectionEngine _engine;
    private readonly ByteTracker _tracker;
    // Reported in the cadence line so the score a real object actually reaches can be read directly
    // against the bar it has to clear, instead of inferred.
    private readonly float _trackerNewTrackThreshold;
    private readonly MovementClassifier _movement = new();
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _runTask;

    // 0 until the first (and only) InputResolutionDetected has been acted on — the event fires again
    // on every reconnect, and an aspect mismatch only needs warning about once per pipeline lifetime.
    private int _streamInfoReported;

    // Frame-cadence accounting (see LogCadenceIfDue). What the inference loop actually achieves per
    // second is the number that decides whether a moving object can be tracked at all — ByteTrack
    // associates by IoU between consecutive processed frames, so a car in transit needs them close
    // enough together to still overlap itself, where a static object does not. Nothing surfaced this
    // before: LatestFrameSlot has always counted published/consumed frames and no one read them, and
    // LastInferenceMilliseconds was only ever logged on the first inference.
    private static readonly TimeSpan CadenceLogInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastCadenceLogUtc = DateTime.UtcNow;
    private long _lastCadencePublished;
    private long _lastCadenceConsumed;

    // Process-wide GC counters, sampled per pipeline over its own window. Every pipeline sees the
    // same numbers — that is the point: a blocking Gen2 collection stalls all six camera loops at
    // once, which is exactly the lockstep 5-8x swing in inference time observed on the recorder
    // (~15 ms across every camera in one 30s window, ~40-75 ms across every camera in the next).
    // GPU contention from the recorder's own NVENC sessions would look identical from inside this
    // process, so these two counters are what tell the two apart rather than guessing.
    private int _lastCadenceGen2;
    private long _lastCadenceAllocatedBytes;

    // What the model found versus what survived tracking, over the cadence window. This is the pair
    // of numbers that localises "an object is visible on screen but never produces a box or a span":
    // a healthy raw count with a tracked count of zero means the detections are real and the tracker
    // is refusing to promote them into tracks (its new-track threshold is the only thing that can do
    // that), whereas a raw count of zero means the model genuinely is not seeing the object and the
    // tracker is blameless. Peaks rather than averages, because an empty frame is the common case and
    // would drown the one frame that matters in an average.
    private int _cadencePeakDetections;
    private int _cadencePeakTracked;
    private double _cadenceBestDetectionScore;

    // Per-label state — see this class's own doc comment for why the grain is the label, not the
    // track. Only ever touched from the single inference loop below, so plain (not concurrent)
    // collections are safe.
    private readonly Dictionary<string, MotionHysteresis> _hysteresisByLabel = new(StringComparer.OrdinalIgnoreCase);

    // Pulled out as its own pure/testable class — see LabelBestFrameTracker's own doc comment for
    // why "best frame" needs two tiers per label rather than one.
    private readonly LabelBestFrameTracker _bestFrames = new();

    // A1 of the snapshot-alignment work: when a high-res re-detection produces a box for a label,
    // that box (Main-stream-pixel-normalized, from a frame we decoded at an exact known instant and
    // shipped a matching eager crop for) is authoritative for the span's snapshot — it beats any
    // Sub-stream continuous-pass candidate, which is normalized against a different resolution/FOV
    // and pinned only to a whole-second seek into the recorded segment. EnqueueReport prefers this
    // over _bestFrames.GetBest(label); cleared alongside _bestFrames.Reset(label) on a real close.
    private readonly Dictionary<string, BestFrame> _authoritativeBestByLabel = new(StringComparer.OrdinalIgnoreCase);

    // Object detection plan pass 2a: turns D-FINE's flickering per-frame label into one stable label
    // per track before it reaches hysteresis/best-frame/reporting — see TrackLabelArbiter's own doc
    // comment for why a single physical object shouldn't fragment into several spans/snapshots just
    // because the classifier's per-frame guess changes.
    private readonly TrackLabelArbiter _labelArbiter = new();

    // Read by the control API's GET /cameras/{id}/detections handler on a request thread, written
    // by the inference loop — a plain volatile reference swap (never mutated in place) is enough:
    // a reader either sees the previous frame's snapshot or the new one, never a half-built list.
    private volatile IReadOnlyList<VisionLiveDetectionBox> _liveSnapshot = [];

    // The capture instant of the frame _liveSnapshot's boxes came from (pass D: the live-overlay
    // relay stamps its payload with this so the browser can delay the boxes to match the video,
    // which runs ~1 GOP + buffer behind). A long, not a DateTime, only because DateTime can't be
    // volatile — written/read atomically as ticks.
    private long _liveSnapshotTicksUtc;

    // Enqueued by the inference loop the instant a span closes, drained by its own loop below —
    // decoupled from the inference loop the same way NodeWorker's own SegmentReportLoopAsync is
    // decoupled from RecordingSession's frame-reading, so a slow/unreachable Node callback can
    // never back-pressure detection itself.
    private readonly ConcurrentQueue<VisionDetectionReportItem> _pendingReports = new();

    // Detection/hardware-acceleration overhaul, pass 3b: one high-res Main-stream re-detection
    // attempt per track, not per frame — a track only ever triggers once, the first frame it starts
    // contributing motion, dequeued and processed by its own loop (HighResReDetectionLoopAsync)
    // decoupled from the continuous Sub-stream inference loop for the same "slow work must never
    // back-pressure the hot loop" reason _pendingReports already is. Pruned alongside
    // _movement/_labelArbiter when ByteTrack drops a track.
    private readonly HashSet<int> _triggeredHighResTrackIds = new();
    private readonly ConcurrentQueue<HighResTrigger> _pendingHighResTriggers = new();
    private readonly string _ffmpegPath;

    // Pass G: a snapshot is cropped from the exact frame the model ran on (this camera's continuous
    // Sub-stream buffer) the first frame a *new* track starts Moving — covering every object moving
    // in that same frame. Once per track like _triggeredHighResTrackIds, plus a per-camera minimum
    // gap so a burst of arrivals produces one crop, not one per track. _frameIsNv12 picks the crop
    // helper (BGRA is the default; nv12 only when GpuPreprocessing is on — never for YOLOX).
    private readonly HashSet<int> _snapshottedTrackIds = new();
    private readonly bool _frameIsNv12;
    private DateTime _lastSubSnapshotUtc = DateTime.MinValue;
    private static readonly TimeSpan SubSnapshotMinGap = TimeSpan.FromSeconds(1);

    // Pass F (Detection.HiResSnapshots, opt-in): the capture buffer is the Sub stream at up to its
    // native resolution rather than the network input size, so TrySubFrameSnapshot has real pixels
    // to crop. _networkScratch non-null is the whole signal that hi-res mode is on — InferenceLoopAsync
    // downscales each capture frame into it (BgraOps.LetterboxResize) before handing it to the engine,
    // which still only ever sees the network size. Null (the default) keeps the frame path
    // byte-identical to before this pass. Only helps where the Sub stream's own resolution exceeds
    // the detector input; a near-no-op otherwise. Incompatible with GpuPreprocessing (forced off).
    private readonly int _captureWidth;
    private readonly int _captureHeight;
    private readonly byte[]? _networkScratch;

    // Checkpoint 3d: a high-res result feeds back into _bestFrames the same way UpdateBestFrameForLabel
    // already does for the continuous Sub-stream pass — reusing the existing best-frame competition
    // (and, downstream, the existing MotionSpan.BestBoxX/Y/W/H columns and lazy /snapshot-image crop)
    // rather than a separate eager-write path, since the box is already normalized 0-1 and persisted
    // per span regardless of which pass produced it. ProcessHighResTriggerAsync runs on its own task
    // (HighResReDetectionLoopAsync), concurrently with InferenceLoopAsync, and _bestFrames is
    // documented not thread-safe (single-inference-loop ownership) — so the result is queued here and
    // applied from inside InferenceLoopAsync itself, never touched directly from the high-res task.
    private readonly record struct HighResResult(int TrackId, string Label, DateTime AtUtc,
        double XNorm, double YNorm, double WNorm, double HNorm, double Confidence);
    private readonly ConcurrentQueue<HighResResult> _pendingHighResResults = new();

    // Shared by every camera's pipeline (owned by CameraPipelineManager, one instance for the whole
    // process) — each trigger spawns its own ffmpeg process plus a batched inference call, and
    // nothing bounded how many of those could run at once *across cameras*: a busy moment on several
    // cameras simultaneously could pile up that many concurrent ffmpeg decodes, pegging node CPU. A
    // single process-wide slot serializes them; there's no snapshot latency cost to that yet since
    // this pass only logs its result (checkpoint 3d, not yet built, is what will actually persist it).
    private readonly SemaphoreSlim _highResGate;

    private readonly record struct HighResTrigger(int TrackId, string Label, DateTime AtUtc, double XNorm, double YNorm, double WNorm, double HNorm);

    public CameraDetectionPipeline(VisionStartCameraRequest request, VisionServiceOptions serviceOptions,
        string resolvedFfmpegPath, string resolvedModelPath, DetectionModelFamily modelFamily, DFineWeights dfineWeights,
        YoloXSize yoloXSize, AspectMode aspectMode, HttpClient http, ILoggerFactory loggerFactory, SemaphoreSlim highResGate)
    {
        _request = request;
        _http = http;
        _ffmpegPath = resolvedFfmpegPath;
        _highResGate = highResGate;
        _logger = loggerFactory.CreateLogger($"Vision[{request.CameraId}]");

        // Detection/hardware-acceleration overhaul, pass 1: request.Width/Height are now this
        // camera's own source aspect ratio, not a decode target — InferenceProfile derives the
        // actual network decode size (and, for Letterbox, the pre-pad scale+pad geometry) from them.
        // Built once here and shared by both VisionSession (ffmpeg's own filter target) and the
        // detection engine (box decode) so the two can never disagree about the transform.
        // YOLOX sizes decode at their own fixed input size (416 for nano/tiny, 640 otherwise) — must
        // match the pinned ONNX export; D-FINE keeps InferenceProfile's 640 default.
        var networkSize = modelFamily == DetectionModelFamily.YoloX
            ? DetectionModelCatalog.GetYoloXNetworkSize(yoloXSize)
            : InferenceProfile.DefaultNetworkSize;

        // Pass F: with Detection.HiResSnapshots on, the capture buffer is the Sub stream at up to its
        // native resolution (capped to SnapshotImageCapture.MaxDimension on the long edge) rather than
        // the network input size, and _profile is built from those capture dims — the letterbox
        // scale/pad depend only on the aspect ratio, so this is transparent to MapBoxToSource and to
        // every _profile.SourceWidth/Height normalization downstream, while making the forward
        // (LetterboxResize) and inverse (decode) transforms self-consistent by construction. The
        // default (off) branch builds the profile from request.Width/Height exactly as before.
        int captureWidth, captureHeight;
        if (request.HiResSnapshots)
        {
            (captureWidth, captureHeight) = ComputeCaptureDimensions(request.Width, request.Height, networkSize);
            _profile = InferenceProfile.Create(captureWidth, captureHeight, aspectMode, networkSize);
            _networkScratch = new byte[networkSize * networkSize * 4];
        }
        else
        {
            _profile = InferenceProfile.Create(request.Width, request.Height, aspectMode, networkSize);
            captureWidth = _profile.NetworkWidth;
            captureHeight = _profile.NetworkHeight;
        }
        _captureWidth = captureWidth;
        _captureHeight = captureHeight;

        // Pass 4a: with GPU preprocessing, ffmpeg emits packed nv12 (W*H*3/2 bytes) and the engine's
        // merged head does the colour convert + normalize on the accelerator; otherwise BGRA (W*H*4)
        // and the engine packs it on the CPU. Forced off for YOLOX — OnnxPreprocessHead is D-FINE-
        // shaped (/255 RGB); a YOLOX variant is a follow-up (see the model-swap plan's E1b). Also
        // forced off when HiResSnapshots is on — the merged nv12 head bakes the network input size at
        // load and can't consume a capture-sized frame (combining the two is a future pass).
        var gpuPreprocessing = request.GpuPreprocessing
            && modelFamily == DetectionModelFamily.DFine
            && !request.HiResSnapshots;
        _frameIsNv12 = gpuPreprocessing;
        var frameBytes = gpuPreprocessing
            ? _profile.NetworkWidth * _profile.NetworkHeight * 3 / 2
            : captureWidth * captureHeight * 4;

        _slot = new LatestFrameSlot(frameBytes);
        _session = new VisionSession(
            new VisionSessionOptions(resolvedFfmpegPath, request.RtspUri, captureWidth, captureHeight,
                request.HardwareAcceleration,
                // No pre-pad step for hi-res: the capture buffer is native-aspect, ffmpeg does a
                // plain scale= and BgraOps.LetterboxResize applies the pad in-process per frame.
                Letterbox: aspectMode == AspectMode.Letterbox && !request.HiResSnapshots
                    ? new LetterboxGeometry(_profile.ScaledWidth, _profile.ScaledHeight, _profile.PadLeft, _profile.PadTop)
                    : null,
                Nv12Output: gpuPreprocessing,
                FpsCap: request.DecodeFpsCap),
            _slot, loggerFactory.CreateLogger<VisionSession>());
        _session.InputResolutionDetected += OnInputResolutionDetected;

        _engine = DetectionEngineFactory.Create(modelFamily, dfineWeights, yoloXSize, new EngineOptions
        {
            ModelPath = resolvedModelPath,
            GpuId = serviceOptions.GpuId,
            CudnnPath = serviceOptions.CudnnPath,
            EnableTensorRt = serviceOptions.EnableTensorRt,
            TensorRtPrecision = serviceOptions.TensorRtPrecision,
            TensorRtEngineCachePath = serviceOptions.TensorRtEngineCachePath,
            TensorRtLibPath = serviceOptions.TensorRtLibPath,
            OpenVinoDeviceType = serviceOptions.OpenVinoDeviceType,
            GpuPreprocessing = gpuPreprocessing,
        }, _profile, loggerFactory);

        // Per-family tracker tuning, anchored to this camera's own detection confidence — see
        // ByteTrackOptions.ForFamily for why the tracker's gates must follow the configured
        // confidence rather than sit at fixed values above it.
        var trackerOptions = ByteTrackOptions.ForFamily(modelFamily, request.Confidence);
        _trackerNewTrackThreshold = trackerOptions.HighThreshold;
        _tracker = new ByteTracker(trackerOptions);

        _runTask = RunAsync(_cts.Token);
    }

    public IReadOnlyList<VisionLiveDetectionBox> GetLiveSnapshot() => _liveSnapshot;

    /// <summary>The capture instant of the frame <see cref="GetLiveSnapshot"/>'s boxes were detected
    /// on — the anchor the live-overlay relay needs to hold boxes back to the video's presentation
    /// time. <c>default</c> until the first frame has been processed.</summary>
    public DateTime GetLiveSnapshotAtUtc()
    {
        var ticks = Interlocked.Read(ref _liveSnapshotTicksUtc);
        return ticks == 0 ? default : new DateTime(ticks, DateTimeKind.Utc);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var sessionTask = _session.RunAsync(ct);
        var inferenceTask = InferenceLoopAsync(ct);
        var checkpointTask = CheckpointLoopAsync(ct);
        var reportFlushTask = ReportFlushLoopAsync(ct);
        var highResTask = HighResReDetectionLoopAsync(ct);

        try
        {
            await Task.WhenAll(sessionTask, inferenceTask, checkpointTask, reportFlushTask, highResTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera detection pipeline for {CameraId} failed.", _request.CameraId);
        }
    }

    private async Task InferenceLoopAsync(CancellationToken ct)
    {
        // One buffer for the whole loop rather than a fresh array per frame. At the detection frame
        // size this is a Large Object Heap allocation every time (640x640 BGRA is 1.56 MB), and on a
        // six-camera node that was the single largest source of allocation in the process. Safe to
        // reuse because everything that touches the frame does so synchronously inside one iteration:
        // _engine.Detect reads it, TrySubFrameSnapshot encodes its JPEG before returning, and nothing
        // retains it past that (PostEagerCropAsync is handed the finished JPEG, not the frame).
        var frameBuffer = new byte[_slot.FrameBytes];

        while (!ct.IsCancellationRequested)
        {
            var captured = await _slot.TakeAsync(frameBuffer, ct);
            if (captured is null) break; // cancelled or disposed
            var frame = captured.Value.Frame;

            List<YoloDotNet.Models.ObjectDetection> detections;
            try
            {
                // Pass F: in hi-res mode `frame` is the (larger) capture buffer — downscale a copy
                // into the reused network scratch for the engine, which still only sees the network
                // size. TrySubFrameSnapshot below still gets the full-resolution `frame`.
                var inferenceFrame = _networkScratch is null
                    ? frame
                    : BgraOps.LetterboxResize(frame, _captureWidth, _captureHeight, _profile, _networkScratch);
                detections = _engine.Detect(inferenceFrame, _request.Confidence, _request.Iou);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Inference failed on one frame — skipping it.");
                continue;
            }

            var tracked = _tracker.Update(detections);

            if (detections.Count > _cadencePeakDetections) _cadencePeakDetections = detections.Count;
            if (tracked.Count > _cadencePeakTracked) _cadencePeakTracked = tracked.Count;
            foreach (var d in detections)
            {
                if (d.Confidence > _cadenceBestDetectionScore) _cadenceBestDetectionScore = d.Confidence;
            }

            LogCadenceIfDue();
            // The instant the reader captured this frame — NOT DateTime.UtcNow read here, which is
            // after TakeAsync waited for a slot and _engine.Detect ran, both of which push it later
            // than the moment the boxes actually describe (a systematic "the object already moved"
            // error on every snapshot). See CapturedFrame's own doc comment.
            var now = captured.Value.CapturedUtc;
            // IDetection.Id is nullable at the interface level (an untracked detection has none),
            // but every detection ByteTracker.Update actually returns has already had a real track
            // id written into it (see ByteTracker.Update's own final step) — the null-filter here is
            // defensive, not an expected case.
            var activeTrackIds = tracked.Where(d => d.Id.HasValue).Select(d => d.Id!.Value).ToHashSet();
            _movement.Prune(activeTrackIds);
            _labelArbiter.Prune(activeTrackIds);
            _triggeredHighResTrackIds.RemoveWhere(id => !activeTrackIds.Contains(id));
            _snapshottedTrackIds.RemoveWhere(id => !activeTrackIds.Contains(id));

            // Checkpoint 3d: apply any high-res re-detection results that finished since the last
            // frame — see _pendingHighResResults' own doc comment for why this has to happen here
            // (the single inference-loop thread) rather than from ProcessHighResTriggerAsync's own
            // task. A track that's already gone by the time its result comes back means the object
            // moved on/disappeared before this could be applied — discarded rather than risking it
            // winning a since-started, unrelated span for the same label.
            while (_pendingHighResResults.TryDequeue(out var hr))
            {
                if (!activeTrackIds.Contains(hr.TrackId)) continue;
                var normalizedArea = Math.Clamp(hr.WNorm * hr.HNorm, 0.0, 1.0);
                var hrFrame = new BestFrame(hr.AtUtc, hr.XNorm, hr.YNorm, hr.WNorm, hr.HNorm, hr.Confidence);
                _bestFrames.Observe(hr.Label, hrFrame, normalizedArea, hr.Confidence);
                _authoritativeBestByLabel[hr.Label] = hrFrame;
            }

            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var liveBoxes = new List<VisionLiveDetectionBox>(tracked.Count);
            // Pass G: every detection that's Moving in THIS frame (label + box), plus whether one of
            // them is a track we haven't snapshotted yet — decided after the loop so the crop covers
            // all of them at once.
            var movingThisFrame = new List<(string Label, YoloDotNet.Models.ObjectDetection Detection)>();
            var newMovingTrack = false;

            foreach (var detection in tracked)
            {
                if (detection.Id is not { } trackId) continue;

                // rawLabel/rawCategory are what the live overlay shows — the viewer should keep
                // seeing what the model actually said this instant. Everything that feeds a span
                // (hysteresis, best-frame tracking, reporting) uses the arbiter's stable label
                // instead, so a track's flickering per-frame guess can't fragment into several
                // independent snapshots — see TrackLabelArbiter's own doc comment.
                var rawLabel = detection.Label?.Name ?? "object";
                var rawCategory = CocoCategoryMap.Resolve(rawLabel);
                var label = _labelArbiter.Resolve(trackId, rawLabel, detection.Confidence);
                var category = CocoCategoryMap.Resolve(label);
                seenLabels.Add(label);

                // _profile.SourceWidth/SourceHeight, not frame.Width/frame.Height — as of pass 1,
                // detection.BoundingBox is reported in the profile's own source-pixel space (see
                // DFineDecoder.Decode's own doc comment), which only equals the captured frame's
                // dimensions when AspectMode is Stretch. Normalizing against the wrong one would
                // silently misplace every box on a Letterbox camera.
                var observation = _movement.Observe(trackId, detection.BoundingBox, detection.Confidence,
                    _profile.SourceWidth, _profile.SourceHeight, now);

                // Idle tracks never open/extend a span unless ReportIdleDetections is on — decision
                // 7's "not interested in static objects" default. A Moving track always does.
                var motionPresent = observation.State == MovementState.Moving || _request.ReportIdleDetections;

                // Only a detection that's actually contributing to this span gets to compete for
                // "best frame" — otherwise a parked/idle object sharing this label (a driveway's
                // own stationary truck, say) can win over the thing that's actually driving the
                // reported span (a car passing through), since it's bigger, better-lit, and more
                // stable every single frame. Computed after motionPresent so a non-contributing
                // detection is skipped entirely, not just deprioritized.
                if (motionPresent)
                {
                    UpdateBestFrameForLabel(label, detection, _profile.SourceWidth, _profile.SourceHeight, now);

                    movingThisFrame.Add((label, detection));
                    // Pass G: a track we've never snapshotted starting to move is what triggers a
                    // fresh Sub-frame snapshot after this loop (once-only per track, same as below).
                    if (_snapshottedTrackIds.Add(trackId)) newMovingTrack = true;

                    // Pass 3b: one high-res Main-stream re-detection per track, fired the first frame
                    // it starts contributing motion — HashSet.Add returns false for a track already
                    // triggered, so this is naturally once-only without a separate lookup.
                    if (_request.EnableHighResReDetection && _triggeredHighResTrackIds.Add(trackId))
                    {
                        _pendingHighResTriggers.Enqueue(new HighResTrigger(trackId, label, now,
                            detection.BoundingBox.Left / (double)_profile.SourceWidth,
                            detection.BoundingBox.Top / (double)_profile.SourceHeight,
                            detection.BoundingBox.Width / (double)_profile.SourceWidth,
                            detection.BoundingBox.Height / (double)_profile.SourceHeight));
                    }
                }

                var hysteresis = GetOrCreateHysteresis(label);
                if (hysteresis.Observe(now, motionPresent, detection.Confidence) is { } closed)
                {
                    EnqueueReport(closed, label, category);
                }

                liveBoxes.Add(new VisionLiveDetectionBox(
                    trackId, rawCategory, rawLabel, observation.State.ToString(),
                    detection.BoundingBox.Left / (double)_profile.SourceWidth,
                    detection.BoundingBox.Top / (double)_profile.SourceHeight,
                    detection.BoundingBox.Width / (double)_profile.SourceWidth,
                    detection.BoundingBox.Height / (double)_profile.SourceHeight,
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
            Interlocked.Exchange(ref _liveSnapshotTicksUtc, now.Ticks);

            // Pass G: a new track started moving this frame — snapshot it (and everything else
            // moving) straight from this exact frame, once the per-camera cadence gate allows.
            if (newMovingTrack && now - _lastSubSnapshotUtc >= SubSnapshotMinGap && movingThisFrame.Count > 0)
            {
                _lastSubSnapshotUtc = now;
                TrySubFrameSnapshot(frame, now, movingThisFrame);
            }
        }
    }

    /// <summary>Once every <see cref="CadenceLogInterval"/>, logs what this camera's pipeline is
    /// actually achieving: frames ffmpeg delivered, frames inference consumed (the difference is
    /// frames dropped in the slot because inference couldn't keep up), and the last inference's own
    /// duration. Called from the inference loop, so it is single-threaded like everything else there.
    ///
    /// Information rather than Debug on purpose — ffmpeg emits nothing on stderr in steady state, so
    /// raising the deployment log level to Debug reveals nothing about throughput, and this is the
    /// only way to tell a starved loop apart from a tracking fault after the fact.</summary>
    private void LogCadenceIfDue()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastCadenceLogUtc;
        if (elapsed < CadenceLogInterval) return;

        var published = _slot.PublishedCount;
        var consumed = _slot.ConsumedCount;
        var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes();

        var deltaPublished = published - _lastCadencePublished;
        var deltaConsumed = consumed - _lastCadenceConsumed;
        var deltaGen2 = gen2 - _lastCadenceGen2;
        var deltaAllocated = allocated - _lastCadenceAllocatedBytes;

        _lastCadenceLogUtc = now;
        _lastCadencePublished = published;
        _lastCadenceConsumed = consumed;
        _lastCadenceGen2 = gen2;
        _lastCadenceAllocatedBytes = allocated;

        var seconds = elapsed.TotalSeconds;
        if (seconds <= 0) return;

        var peakDetections = _cadencePeakDetections;
        var peakTracked = _cadencePeakTracked;
        var bestScore = _cadenceBestDetectionScore;
        _cadencePeakDetections = 0;
        _cadencePeakTracked = 0;
        _cadenceBestDetectionScore = 0;

        // The live snapshot as the viewer's overlay would draw it, so a "there is clearly a car on
        // screen and no box on it" report can be checked against what the pipeline actually held at
        // that moment rather than reasoned about.
        var live = _liveSnapshot;
        var liveSummary = live.Count == 0
            ? "none"
            : string.Join(", ", live.Take(10).Select(b => $"{b.Label}/{b.MovementState}/{b.Confidence:F2}"));

        _logger.LogInformation(
            "Camera {CameraId} detection cadence over {Seconds:F0}s: capture {CaptureFps:F1} fps, " +
            "inference {InferenceFps:F1} fps, {Dropped} frame(s) dropped ({DropPercent:F0}%), " +
            "last inference {InferenceMs:F1} ms. Peak {PeakDetections} detection(s)/frame -> " +
            "{PeakTracked} tracked, best raw score {BestScore:F2} (tracker needs {NewTrackThreshold:F2} " +
            "to start a track). Live boxes now: {LiveBoxes}. Process-wide over the same window: " +
            "{Gen2} gen2 collection(s), {AllocatedMbPerSec:F0} MB/s allocated.",
            _request.CameraId, seconds, deltaPublished / seconds, deltaConsumed / seconds,
            deltaPublished - deltaConsumed,
            deltaPublished == 0 ? 0 : (deltaPublished - deltaConsumed) * 100.0 / deltaPublished,
            _engine.LastInferenceMilliseconds ?? 0,
            peakDetections, peakTracked, bestScore, _trackerNewTrackThreshold, liveSummary,
            deltaGen2, deltaAllocated / seconds / (1024.0 * 1024.0));
    }

    /// <summary>Pass G: crops one JPEG from the frame the model just ran on, covering the union of
    /// every currently-Moving box, and makes it the authoritative snapshot for each of those labels'
    /// spans. The box came out of these exact pixels, so — unlike a later timestamp lookup into
    /// recorded footage — the object cannot have moved off the crop. ~150-300 px at the network
    /// buffer size; Pass F (<c>Detection.HiResSnapshots</c>) routes through
    /// <see cref="HiResSubFrameSnapshot"/> to crop from a larger capture buffer instead.</summary>
    private void TrySubFrameSnapshot(byte[] frame, DateTime nowUtc, List<(string Label, YoloDotNet.Models.ObjectDetection Detection)> moving)
    {
        try
        {
            // Pass F: `frame` is the (larger) capture buffer and the crop works in capture-pixel
            // space, with no letterbox pad to route around.
            if (_networkScratch is not null)
            {
                HiResSubFrameSnapshot(frame, nowUtc, moving);
                return;
            }

            var content = _profile.ContentRect;

            SKRectI BoxOf(SKRectI b) // source-pixel box -> network-buffer pixels
            {
                var (x0, y0, x1, y1) = _profile.NormalizedSourceToNetworkPixels(
                    b.Left / (double)_profile.SourceWidth, b.Top / (double)_profile.SourceHeight,
                    b.Right / (double)_profile.SourceWidth, b.Bottom / (double)_profile.SourceHeight);
                return new SKRectI(x0, y0, x1, y1);
            }

            SKRectI? union = null;
            foreach (var (_, det) in moving)
            {
                var px = BoxOf(det.BoundingBox);
                union = union is { } u
                    ? new SKRectI(Math.Min(u.Left, px.Left), Math.Min(u.Top, px.Top),
                        Math.Max(u.Right, px.Right), Math.Max(u.Bottom, px.Bottom))
                    : px;
            }
            if (union is not { } unionRect || unionRect.Width < 2 || unionRect.Height < 2) return;

            // Small margin (a same-frame crop has no cross-stream drift to hide — see
            // SnapshotImageCapture.DefaultMarginFraction's doc comment), then never outside the real
            // image (a wide margin would otherwise reach into the letterbox bars).
            var (mcx, mcy, mcw, mch) = SnapshotImageCapture.ComputeCropRect(
                unionRect.Left / (double)_profile.NetworkWidth, unionRect.Top / (double)_profile.NetworkHeight,
                unionRect.Width / (double)_profile.NetworkWidth, unionRect.Height / (double)_profile.NetworkHeight,
                _profile.NetworkWidth, _profile.NetworkHeight, marginFraction: 0.12);
            var crop = ClampTo(new SKRectI(mcx, mcy, mcx + mcw, mcy + mch), content);

            // Guard: a person at one edge and a car at the other shouldn't degrade into a nearly
            // whole-frame crop — fall back to the single highest-confidence box.
            if (crop.Width > content.Width * 0.8 || crop.Height > content.Height * 0.8)
            {
                crop = ClampTo(BoxOf(moving.OrderByDescending(m => m.Detection.Confidence).First().Detection.BoundingBox), content);
                if (crop.Width < 2 || crop.Height < 2) return;
            }

            // BGRA path takes the final (margined, content-clamped) rect; the nv12 path reuses
            // Nv12Ops.CropToJpeg, which applies its own margin + frame clamp, so it gets the raw
            // union instead. nv12 only happens with GpuPreprocessing on (D-FINE only, off by default).
            var jpeg = _frameIsNv12
                ? Nv12Ops.CropToJpeg(frame, _profile.NetworkWidth, _profile.NetworkHeight, ClampTo(unionRect, content), EagerCropJpegQuality)
                : BgraOps.CropRectToJpeg(frame, _profile.NetworkWidth, _profile.NetworkHeight, crop, EagerCropJpegQuality);
            if (jpeg is null) return;

            _ = PostEagerCropAsync(nowUtc, jpeg);

            // Make this exact frame authoritative for every span it covers, so the span's report
            // cites nowUtc and the /snapshot-image bestFrameTicks lookup finds the file we just
            // posted (same mechanism the high-res path uses). Box stored normalized 0-1 in source
            // space, matching every other BestFrame.
            foreach (var (label, det) in moving)
            {
                var b = det.BoundingBox;
                _authoritativeBestByLabel[label] = new BestFrame(nowUtc,
                    b.Left / (double)_profile.SourceWidth, b.Top / (double)_profile.SourceHeight,
                    b.Width / (double)_profile.SourceWidth, b.Height / (double)_profile.SourceHeight,
                    det.Confidence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sub-frame snapshot failed for camera {CameraId} — that span falls back to the segment-seek crop.", _request.CameraId);
        }
    }

    /// <summary>Intersects a rect with <paramref name="bounds"/> — the codebase hand-rolls rect math
    /// (see Nms.IoU) rather than depend on a particular SkiaSharp helper surface.</summary>
    private static SKRectI ClampTo(SKRectI r, SKRectI bounds) => new(
        Math.Max(r.Left, bounds.Left), Math.Max(r.Top, bounds.Top),
        Math.Min(r.Right, bounds.Right), Math.Min(r.Bottom, bounds.Bottom));

    /// <summary>Pass F: the hi-res <see cref="TrySubFrameSnapshot"/> path. <paramref name="frame"/> is
    /// the capture buffer at (up to) native Sub-stream resolution; there is no letterbox pad in it,
    /// and the detection boxes are already in <c>_profile</c> source space, which for a hi-res
    /// pipeline <i>is</i> capture-pixel space — so this needs neither the network-buffer remap nor a
    /// content-rect that excludes pad bars.</summary>
    private void HiResSubFrameSnapshot(byte[] frame, DateTime nowUtc, List<(string Label, YoloDotNet.Models.ObjectDetection Detection)> moving)
    {
        var content = new SKRectI(0, 0, _captureWidth, _captureHeight);

        SKRectI? union = null;
        foreach (var (_, det) in moving)
        {
            var px = det.BoundingBox;
            union = union is { } u
                ? new SKRectI(Math.Min(u.Left, px.Left), Math.Min(u.Top, px.Top),
                    Math.Max(u.Right, px.Right), Math.Max(u.Bottom, px.Bottom))
                : px;
        }
        if (union is not { } unionRect || unionRect.Width < 2 || unionRect.Height < 2) return;

        var (mcx, mcy, mcw, mch) = SnapshotImageCapture.ComputeCropRect(
            unionRect.Left / (double)_captureWidth, unionRect.Top / (double)_captureHeight,
            unionRect.Width / (double)_captureWidth, unionRect.Height / (double)_captureHeight,
            _captureWidth, _captureHeight, marginFraction: 0.12);
        var crop = ClampTo(new SKRectI(mcx, mcy, mcx + mcw, mcy + mch), content);

        // Same guard as the network-space path: a person at one edge and a car at the other
        // shouldn't degrade into a near-whole-frame crop.
        if (crop.Width > content.Width * 0.8 || crop.Height > content.Height * 0.8)
        {
            crop = ClampTo(moving.OrderByDescending(m => m.Detection.Confidence).First().Detection.BoundingBox, content);
            if (crop.Width < 2 || crop.Height < 2) return;
        }

        var jpeg = BgraOps.CropRectToJpeg(frame, _captureWidth, _captureHeight, crop, EagerCropJpegQuality);
        if (jpeg is null) return;

        _ = PostEagerCropAsync(nowUtc, jpeg);

        foreach (var (label, det) in moving)
        {
            var b = det.BoundingBox;
            _authoritativeBestByLabel[label] = new BestFrame(nowUtc,
                b.Left / (double)_profile.SourceWidth, b.Top / (double)_profile.SourceHeight,
                b.Width / (double)_profile.SourceWidth, b.Height / (double)_profile.SourceHeight,
                det.Confidence);
        }
    }

    /// <summary>Pass F: the capture-buffer dimensions for a hi-res pipeline — the Sub stream's own
    /// aspect ratio with its long edge clamped to <c>[networkSize, SnapshotImageCapture.MaxDimension]</c>
    /// (never below the network size, so the inference downscale is only ever a downscale; never above
    /// the eager-crop JPEG cap, past which resolution is thrown away anyway) and both edges
    /// even-aligned (4:2:0 decode targets, same convention as <c>InferenceProfile.CreateLetterbox</c>).
    /// A Sub stream already at or below the network size comes back at the network size — Pass F is
    /// then a no-op beyond routing the pad step in-process.</summary>
    internal static (int Width, int Height) ComputeCaptureDimensions(int sourceWidth, int sourceHeight, int networkSize)
    {
        var sourceLong = Math.Max(sourceWidth, sourceHeight);
        var targetLong = Math.Clamp(sourceLong, networkSize, SnapshotImageCapture.MaxDimension);

        var scale = targetLong / (double)sourceLong;
        var w = (int)Math.Round(sourceWidth * scale);
        var h = (int)Math.Round(sourceHeight * scale);

        w = Math.Max(2, w - w % 2);
        h = Math.Max(2, h - h % 2);
        return (w, h);
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
        var frame = new BestFrame(nowUtc, x, y, w, h, detection.Confidence);
        _bestFrames.Observe(label, frame, normalizedArea, detection.Confidence);
    }

    private MotionHysteresis GetOrCreateHysteresis(string label)
    {
        if (!_hysteresisByLabel.TryGetValue(label, out var hysteresis))
        {
            // Zero startAfter: ByteTrack's own two-stage association plus its "confirmed only after
            // a second corroborating frame" activation rule already provide the "is this real"
            // gating a startAfter debounce exists for elsewhere. endAfter is NOT zero, unlike that —
            // a zero grace period closed a label's span on the very first quiet frame, so a single
            // missed/occluded detection (or a track dipping into Idle for a moment before resuming
            // Moving) reopened a brand-new span/snapshot instead of continuing the one already in
            // progress. IdleTimeoutSeconds gives real slack to absorb that flicker while still
            // finalizing the snapshot once the object is genuinely gone or has settled into Idle.
            hysteresis = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.FromSeconds(_request.IdleTimeoutSeconds));
            _hysteresisByLabel[label] = hysteresis;
        }
        return hysteresis;
    }

    /// <summary>Reports one span. <paramref name="spanClosed"/> is true when the span has genuinely
    /// ended (hysteresis just closed it, or a shutdown Flush forced it) versus a mid-span checkpoint
    /// of one still in progress — only a real close resets this label's best-frame candidates, so a
    /// checkpoint doesn't throw away bookkeeping the span itself is still going to need.</summary>
    private void EnqueueReport(MotionSpanResult span, string label, string category, bool spanClosed = true)
    {
        // A1: a high-res re-detection box, if this span got one, is authoritative for the snapshot —
        // it's normalized against the Main stream (what the crop is actually taken from) and has a
        // matching eager crop staged on the node. Otherwise fall back to the continuous pass's own
        // best frame: prefer the normal-sized candidate; an oversized one (see LabelBestFrameTracker's
        // own doc comment) only stands in when nothing normal-sized was ever seen for this span.
        BestFrame? best = _authoritativeBestByLabel.TryGetValue(label, out var authoritative)
            ? authoritative
            : _bestFrames.GetBest(label);
        _pendingReports.Enqueue(new VisionDetectionReportItem(
            _request.CameraId, span.StartUtc, span.EndUtc, span.PeakScore,
            category, label,
            best?.AtUtc, best?.X, best?.Y, best?.W, best?.H, best?.Confidence));

        // A closed span is done contributing best-frame candidates — reset so a later, separate
        // span for this same label runs its own fresh contest instead of being handed a stale
        // sighting (a truck that won weeks ago) that could otherwise never lose to a real detection.
        if (spanClosed)
        {
            _bestFrames.Reset(label);
            _authoritativeBestByLabel.Remove(label);
        }
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
                    EnqueueReport(span, label, CocoCategoryMap.Resolve(label), spanClosed: false);
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

    /// <summary>Pass 3b: drains high-res re-detection triggers, decoupled from the continuous
    /// Sub-stream inference loop for the same reason ReportFlushLoopAsync is decoupled from it — a
    /// Node round trip + ffmpeg decode + batched inference is much heavier than one Sub-stream frame
    /// tick and must never stall it. Returns immediately (a permanent no-op for this pipeline's
    /// lifetime) if the feature isn't enabled, this camera's Main stream resolution isn't known yet,
    /// or the loaded engine doesn't support batched inference at all.</summary>
    private async Task HighResReDetectionLoopAsync(CancellationToken ct)
    {
        if (!_request.EnableHighResReDetection) return;

        if (_request.MainStreamWidth is not { } mainWidth || _request.MainStreamHeight is not { } mainHeight)
        {
            _logger.LogWarning(
                "High-res re-detection is enabled for camera {CameraId} but its Main stream resolution isn't known yet — skipping until the next restart.",
                _request.CameraId);
            return;
        }

        if (_engine is not IBatchDetectionEngine batchEngine)
        {
            _logger.LogInformation(
                "High-res re-detection is enabled for camera {CameraId} but the loaded detection engine doesn't support batched inference — skipping.",
                _request.CameraId);
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            if (!_pendingHighResTriggers.TryDequeue(out var trigger))
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(200), ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await _highResGate.WaitAsync(ct);
                try
                {
                    // The process-wide gate can hold a trigger for seconds under multi-camera load;
                    // by the time it's our turn the object may be well outside the box the trigger
                    // captured on the track's first motion frame. Re-detecting a stale instant wastes
                    // a decode and risks a wrong-object match, so drop it — the continuous pass keeps
                    // tracking, and a later frame of this same track can't re-trigger (once per track
                    // id) but the span still gets its Sub-stream best frame.
                    if (DateTime.UtcNow - trigger.AtUtc > TimeSpan.FromSeconds(3))
                    {
                        _logger.LogDebug("Skipping a stale high-res trigger for camera {CameraId}, track {TrackId} ({Age:0.0}s old).",
                            _request.CameraId, trigger.TrackId, (DateTime.UtcNow - trigger.AtUtc).TotalSeconds);
                    }
                    else
                    {
                        await ProcessHighResTriggerAsync(trigger, mainWidth, mainHeight, batchEngine, ct);
                    }
                }
                finally
                {
                    _highResGate.Release();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "High-res re-detection failed for camera {CameraId}, track {TrackId}.", _request.CameraId, trigger.TrackId);
            }
        }
    }

    /// <summary>Fetches the Main-stream instant this trigger points at from the node's own ring
    /// buffer, decodes it once, runs one batched inference call over the whole-frame letterboxed pass
    /// plus every native-scale tile the trigger's own centroid places, and merges the result with NMS
    /// — see this pass's own plan for the SAHI-style reasoning. Deliberately stops at logging the
    /// merged result: wiring it into an eagerly-written snapshot file needs its own file-naming
    /// design (today's cache keys by a MotionSpan id that doesn't exist yet at this point in the
    /// pipeline) and is the next checkpoint's job, not this one's.</summary>
    private async Task ProcessHighResTriggerAsync(HighResTrigger trigger, int mainWidth, int mainHeight,
        IBatchDetectionEngine batchEngine, CancellationToken ct)
    {
        var mainFrameUrl = $"{_request.NodeCallbackBaseUrl.TrimEnd('/')}/internal/main-frame/{_request.CameraId}" +
            $"?atUtc={Uri.EscapeDataString(trigger.AtUtc.ToString("o"))}";

        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(mainFrameUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not reach this node's main-frame buffer for camera {CameraId} — not retrying this specific trigger.", _request.CameraId);
            return;
        }

        // A 404 (nothing buffered for this instant — the trigger's own instant already aged out of
        // the ring buffer's short window) is an expected, common outcome, not a failure worth logging.
        if (!response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsByteArrayAsync(ct);
        if (body.Length < 4) return;
        var initLength = BitConverter.ToInt32(body, 0);
        if (initLength < 0 || 4 + initLength > body.Length) return; // malformed — defensive, shouldn't happen
        var initSegment = body.AsSpan(4, initLength).ToArray();
        var fragment = body.AsSpan(4 + initLength).ToArray();

        var nativeNv12 = await MainFrameDecoder.DecodeNv12Async(_ffmpegPath, initSegment, fragment, mainWidth, mainHeight, ct, _logger,
            hardwareAcceleration: _request.HardwareAcceleration);
        if (nativeNv12 is null) return;
        // DecodeNv12Async forces even dims; use the same values for every downstream calc.
        mainWidth &= ~1;
        mainHeight &= ~1;

        SaveNv12DebugImage(nativeNv12, mainWidth, mainHeight, $"{_request.CameraId}_{trigger.TrackId}_wholeframe-source");

        // The same network size the continuous Sub-stream pipeline's own _profile uses (640).
        var tileSize = _profile.NetworkWidth;
        var wholeFrameProfile = InferenceProfile.Create(mainWidth, mainHeight, AspectMode.Letterbox, tileSize);
        var triggerPoint = new TileLayout.TriggerPoint(trigger.XNorm, trigger.YNorm, trigger.WNorm, trigger.HNorm);
        var tiles = TileLayout.PlaceTiles([triggerPoint], tileSize, mainWidth, mainHeight);

        var wholeFrameNv12 = Nv12Ops.LetterboxTo(nativeNv12, mainWidth, mainHeight, wholeFrameProfile);
        var tileNv12s = tiles.Select(t => Nv12Ops.CropTile(nativeNv12, mainWidth, mainHeight, t)).ToList();

        SaveNv12DebugImage(wholeFrameNv12, tileSize, tileSize, $"{_request.CameraId}_{trigger.TrackId}_wholeframe-letterboxed");
        for (var i = 0; i < tileNv12s.Count; i++)
            SaveNv12DebugImage(tileNv12s[i], tiles[i].Width, tiles[i].Height, $"{_request.CameraId}_{trigger.TrackId}_tile{i}");

        {
            var images = new List<(byte[] Nv12, InferenceProfile Profile)> { (wholeFrameNv12, wholeFrameProfile) };
            for (var t = 0; t < tiles.Count; t++)
            {
                // Source == network == tile size, Stretch mode: MapBoxToSource degenerates to an
                // identity transform, so DFineDecoder.Decode hands back each tile's own boxes already
                // in that tile's own local pixel space — exactly what MapTileBoxToFrame expects.
                var tileProfile = InferenceProfile.Create(tiles[t].Width, tiles[t].Height, AspectMode.Stretch, tileSize);
                images.Add((tileNv12s[t], tileProfile));
            }

            var batchResults = batchEngine.DetectBatch(images, _request.Confidence);

            var merged = new List<(SKRectI Box, double Confidence, string Label)>();
            foreach (var d in batchResults[0])
            {
                merged.Add((d.BoundingBox, d.Confidence, d.Label?.Name ?? "object"));
            }
            for (var t = 0; t < tiles.Count; t++)
            {
                foreach (var d in batchResults[t + 1])
                {
                    var (fx, fy, fw, fh) = TileLayout.MapTileBoxToFrame(tiles[t], d.BoundingBox.Left, d.BoundingBox.Top, d.BoundingBox.Width, d.BoundingBox.Height);
                    merged.Add((new SKRectI(fx, fy, fx + fw, fy + fh), d.Confidence, d.Label?.Name ?? "object"));
                }
            }

            var final = Nms.Suppress(merged, m => m.Box, m => m.Confidence);

            // Debug, not Information: this fires several times per trigger and there can be thousands
            // of triggers an hour on a busy camera — it flooded the vision log. Turn the Vision
            // Service's log level up when a merge bug vs. a genuine detection failure needs telling
            // apart.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var m in final)
                {
                    _logger.LogDebug(
                        "High-res re-detection, camera {CameraId} track {TrackId}: {Label} ({Confidence:P0}) at ({X},{Y}) {Width}x{Height} in {MainWidth}x{MainHeight} Main-stream pixels ({TileCount} native tile(s) placed).",
                        _request.CameraId, trigger.TrackId, m.Label, m.Confidence, m.Box.Left, m.Box.Top, m.Box.Width, m.Box.Height, mainWidth, mainHeight, tiles.Count);
                }
            }

            // Checkpoint 3d: feed the result back into the label's best-frame competition. Matched by
            // overlap against the trigger's own (coarse, Sub-stream-derived) box, not by label — the
            // native-scale pass can genuinely disagree with the arbiter's stabilized label on what the
            // object *is* (a cat re-detected as car/truck/bird, confirmed live), but that's an accuracy
            // question for a different label-arbitration pass, not a reason to distrust *where* it is.
            // Deliberately keeps the trigger's own arbiter-stabilized label rather than the
            // re-detection's — every other candidate this label competes against downstream already
            // goes through that same stabilization (see TrackLabelArbiter's own doc comment), so a
            // one-off native-scale guess shouldn't get a special exemption from it. No match (IoU 0 —
            // the object moved on, or was never really there) means nothing is queued; today's coarser
            // Sub-stream candidate stays the best one on record, which is only ever a wash, never a
            // regression.
            var triggerBoxPx = new SKRectI(
                (int)Math.Round(trigger.XNorm * mainWidth), (int)Math.Round(trigger.YNorm * mainHeight),
                (int)Math.Round((trigger.XNorm + trigger.WNorm) * mainWidth), (int)Math.Round((trigger.YNorm + trigger.HNorm) * mainHeight));
            if (Nms.FindBestMatch(final, triggerBoxPx, m => m.Box) is { } matched)
            {
                _pendingHighResResults.Enqueue(new HighResResult(trigger.TrackId, trigger.Label, trigger.AtUtc,
                    matched.Box.Left / (double)mainWidth, matched.Box.Top / (double)mainHeight,
                    matched.Box.Width / (double)mainWidth, matched.Box.Height / (double)mainHeight,
                    matched.Confidence));

                // A1: crop the snapshot from THIS frame (the exact Main-stream instant) using the
                // re-detected box — not the stale trigger centroid. The POST is detached so a slow
                // node round trip can't hold the process-wide gate.
                var eagerCrop = Nv12Ops.CropToJpeg(nativeNv12, mainWidth, mainHeight, matched.Box, EagerCropJpegQuality);
                if (eagerCrop is not null)
                    _ = PostEagerCropAsync(trigger.AtUtc, eagerCrop);
            }
        }
    }

    private const int EagerCropJpegQuality = 88;

    private void SaveNv12DebugImage(byte[] nv12, int w, int h, string fileName)
    {
        if (!_request.EnableVisionDebugImages) return;
        using var bitmap = Nv12Ops.ToDebugBitmap(nv12, w, h);
        SaveDebugImage(bitmap, fileName);
    }

    // Diagnostic — see ProcessHighResTriggerAsync's own call sites. Writes into the same shared logs
    // directory Vision Service's own FileLoggerProvider now uses, under a dedicated subfolder,
    // best-effort (a failure to write a debug image must never break real detection). Gated by the
    // node-scoped Detection.EnableVisionDebugImages setting — a no-op unless an admin turns it on.
    private void SaveDebugImage(SKBitmap bitmap, string fileName)
    {
        if (!_request.EnableVisionDebugImages) return;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "logs", "vision-debug");
            Directory.CreateDirectory(dir);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            using var stream = File.OpenWrite(Path.Combine(dir, $"{fileName}.jpg"));
            data.SaveTo(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write debug image {FileName} — harmless (diagnostic-only), but logged at Warning specifically so this failure itself is actually visible.", fileName);
        }
    }

    // The watch stream is never ffmpeg-probed on the node side, so _request.Width/Height (what
    // InferenceProfile, the ffmpeg scale/pad filter, and every snapshot crop's geometry were built
    // from) can be a stale ONVIF value, the Main-stream aspect as a stand-in, or the 1280x720
    // fallback. VisionSession reports what ffmpeg actually decoded; if its aspect ratio disagrees with
    // what we were started with, this pipeline is feeding the model a squashed frame and every crop
    // comes off that same distorted buffer (a portrait camera set up as landscape is the severe case).
    //
    // Diagnostic only — deliberately. 0.172.0 posted these dimensions back so the node could persist
    // them and restart the watch, and that could not hold: CameraService.ReplaceStreamsAsync
    // overwrites CameraStream.Width/Height from ONVIF on every re-probe (and a re-probe runs on every
    // web app restart), so the correction was reverted and the watch restarted in a loop, discarding
    // this camera's ByteTrack state each cycle. The fix is the camera's own AiDetection.Orientation
    // setting, which lives where a re-probe can't reach it — so all this does now is name it.
    private void OnInputResolutionDetected(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (Interlocked.Exchange(ref _streamInfoReported, 1) != 0) return;

        var assumedAspect = (double)_request.Width / _request.Height;
        var actualAspect = (double)width / height;
        var drift = Math.Abs(assumedAspect - actualAspect) / actualAspect;
        if (drift <= 0.02) return; // within 2% — the geometry is close enough, nothing to correct

        _logger.LogWarning(
            "Camera {CameraId}: AI detection was set up for a {AssumedWidth}x{AssumedHeight} stream but " +
            "ffmpeg is actually decoding {ActualWidth}x{ActualHeight}. The model is running on a distorted " +
            "frame and snapshot crops will look stretched — set this camera's AI detection orientation to " +
            "{Orientation} (Cameras > Edit, or the deployment default on Admin > Settings > Detection).",
            _request.CameraId, _request.Width, _request.Height, width, height,
            height > width ? "Portrait" : "Landscape");
    }

    private async Task PostEagerCropAsync(DateTime atUtc, byte[] jpeg)
    {
        try
        {
            var url = $"{_request.NodeCallbackBaseUrl.TrimEnd('/')}/detections/crop";
            var response = await _http.PostAsJsonAsync(url, new VisionDetectionCropItem(_request.CameraId, atUtc, jpeg), _cts.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not ship an eager snapshot crop for camera {CameraId} — that span will fall back to the segment-seek crop.", _request.CameraId);
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

        _session.InputResolutionDetected -= OnInputResolutionDetected;
        _cts.Dispose();
        _slot.Dispose();
        _engine.Dispose();
    }
}
