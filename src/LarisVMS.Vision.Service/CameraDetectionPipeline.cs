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
///
/// Detection/hardware-acceleration overhaul, pass 4 (<see cref="AspectMode.Slice"/>): mandatorily
/// GPU-preprocessed — there is deliberately no CPU fallback for cutting slices. Slicing exists
/// specifically to put more real pixels on target than Letterbox's black-bar padding allows, and
/// cutting a captured frame into slices on the CPU (decode → download → crop N times → re-upload)
/// is exactly the "CPU back-and-forth" the accelerator-side slice (a Slice+Concat pair merged into
/// the model's own ONNX graph, see <see cref="OnnxPreprocessHead.MergeSliced"/>) exists to avoid.
/// A node with no usable GPU simply cannot run Slice mode — it can still run Letterbox/Stretch.
/// </summary>
public sealed class CameraDetectionPipeline : IAsyncDisposable
{
    // Same cadence NodeWorker.EnqueueMotionCheckpoints already uses for the equivalent purpose.
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckpointRecency = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReportFlushInterval = TimeSpan.FromSeconds(2);

    private readonly VisionStartCameraRequest _request;
    // Non-Slice modes: the camera's own real InferenceProfile (SourceWidth/Height == this camera's
    // real dims, NetworkWidth/Height == the square decode target). Slice mode: the shared per-slice
    // *identity* profile every slice's raw model output decodes through (Stretch, network-size
    // square) — never this camera's own real dims, which is exactly why _sourceWidth/_sourceHeight
    // below exist as their own fields instead of every caller reading _profile.SourceWidth/Height.
    private readonly InferenceProfile _profile;
    // Detection/hardware-acceleration overhaul, pass 4: non-null only for AspectMode.Slice — see
    // this class's own doc comment. Owns the whole per-camera capture/slice geometry that a Slice
    // camera has no single InferenceProfile for.
    private readonly SliceLayout? _sliceLayout;
    // This camera's own real aspect-ratio pixel dimensions — the coordinate space every reported
    // box, live-overlay box and snapshot crop is normalized against, regardless of AspectMode. Equal
    // to _profile.SourceWidth/SourceHeight for Letterbox/Stretch (kept as separate fields anyway so
    // Slice mode — whose _profile carries a completely different, non-camera-specific meaning — needs
    // no special-casing at any of this class's many read sites).
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly VisionSession _session;
    private readonly LatestFrameSlot _slot;

    // Built by InferenceLoopAsync rather than in the constructor, so /start never blocks on it: with
    // TensorRT the first build for a given model compiles an engine, which takes minutes and
    // saturates every core. Doing that inside the request handler held a Kestrel thread for the whole
    // build, once per camera concurrently, which starved the node's own recording ffmpeg drain loops
    // badly enough that the tee muxer blocked and recording *and* live view stopped together (see
    // EngineBuildGate for the other half of that fix). Null until the build completes.
    private volatile IDetectionEngine? _engine;
    private readonly EngineOptions _engineOptions;
    private readonly DetectionModelFamily _modelFamily;
    private readonly DFineWeights _dfineWeights;
    private readonly YoloXSize _yoloXSize;
    private readonly ILoggerFactory _loggerFactory;

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

    // Slice-mode accounting, same window and the same reasoning one level further in: once slicing is
    // on, "the model found nothing" has two very different causes that the counters above cannot
    // separate. Raw detections per slice say whether each slice is contributing at all (a slice stuck
    // at zero is a geometry or preprocessing fault, not a model one); the two absorption counts say
    // whether SliceMerge is reuniting anything, and specifically whether its *seam* rule — the whole
    // reason that class exists — has ever fired. "Detected inside one slice but never across the
    // seam" is exactly the report these exist to make diagnosable instead of arguable.
    // Totals over the window, not peaks: what matters here is whether a thing ever happens at all.
    private int[]? _cadenceSliceDetections;
    private int _cadenceIouAbsorbed;
    private int _cadenceSeamAbsorbed;
    private int _cadenceSpanningMerges;

    // Inference failures are counted rather than logged per frame. A persistent fault (a wrong
    // TensorRT engine, say) fails every frame, and at the capture rate that is ten identical stack
    // traces per second per camera — which is how the 0.186.0 engine cache collision buried every
    // other line in the vision log. The first one still logs in full; the rest become a count here.
    private int _cadenceInferenceFailures;
    private bool _loggedInferenceFailure;

    // Per-label state — see this class's own doc comment for why the grain is the label, not the
    // track. Only ever touched from the single inference loop below, so plain (not concurrent)
    // collections are safe.
    private readonly Dictionary<string, MotionHysteresis> _hysteresisByLabel = new(StringComparer.OrdinalIgnoreCase);

    // Pulled out as its own pure/testable class — see LabelBestFrameTracker's own doc comment for
    // why "best frame" needs two tiers per label rather than one.
    private readonly LabelBestFrameTracker _bestFrames = new();

    // A1 of the snapshot-alignment work: a pass G eager crop's own box (Sub-stream-pixel-normalized,
    // from a frame we decoded at an exact known instant and shipped a matching eager crop for) is
    // authoritative for the span's snapshot — it beats the continuous pass's own best-frame
    // candidate, which is otherwise pinned only to a whole-second seek into the recorded segment.
    // EnqueueReport prefers this over _bestFrames.GetBest(label); cleared alongside
    // _bestFrames.Reset(label) on a real close.
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

    // Pass G: a snapshot is cropped from the exact frame the model ran on (this camera's continuous
    // Sub-stream buffer) the first frame a *new* track starts Moving — covering every object moving
    // in that same frame. Once per track at minimum (see _stagedSnapshotScore for the progressive
    // upgrade on top), plus a per-camera minimum gap so a burst of arrivals produces one crop, not
    // one per track. _frameIsNv12 picks the crop helper (BGRA is the default; nv12 only when
    // GpuPreprocessing is on — never for YOLOX).
    private readonly HashSet<int> _snapshottedTrackIds = new();
    private readonly bool _frameIsNv12;
    private DateTime _lastSubSnapshotUtc = DateTime.MinValue;
    private static readonly TimeSpan SubSnapshotMinGap = TimeSpan.FromSeconds(1);

    // Progressive best snapshot, per camera (not per label): the CompositeFrameScore of whichever
    // frame is currently staged as the eager crop for this camera's in-progress detections — the
    // count of moving objects it captured plus their cumulative confidence*area. A later frame only
    // replaces the staged crop (and only triggers a fresh crop+upload at all) when it captures more
    // moving objects, or the same number more clearly (SnapshotImprovementMargin) — so the snapshot
    // keeps upgrading toward the frame that best shows *every* moving object at once, instead of
    // drifting to whichever frame the one dominant object peaked in. Reset in EnqueueReport on the
    // span close that empties _authoritativeBestByLabel (every co-detected label finished), so a
    // later, separate burst of activity starts its own fresh contest — but kept across a brief
    // mid-span track loss so a good staged crop isn't discarded.
    private CompositeFrameScore _stagedSnapshotScore;

    // Relative, not absolute: MovementClassifier.Score has no fixed scale (it's confidence * area,
    // both already 0-1), and a bare "any improvement, however tiny" comparison would re-crop and
    // re-upload almost every frame while an object cruises steadily through the scene, since the
    // score jitters slightly frame to frame even when nothing meaningful changed.
    private const double SnapshotImprovementMargin = 0.15;

    private const int EagerCropJpegQuality = 88;

    public CameraDetectionPipeline(VisionStartCameraRequest request, VisionServiceOptions serviceOptions,
        string resolvedFfmpegPath, string resolvedModelPath, DetectionModelFamily modelFamily, DFineWeights dfineWeights,
        YoloXSize yoloXSize, AspectMode aspectMode, string dfineTensorRtMode, HttpClient http, ILoggerFactory loggerFactory)
    {
        _request = request;
        _http = http;
        // The camera's own name rather than its GUID — see VisionStartCameraRequest.DisplayName. The
        // full id still reaches the log on the pipeline-start line below and on every failure, so a
        // line here can always be tied back to a camera row; it just isn't repeated 36 characters at
        // a time on every routine line.
        _logger = loggerFactory.CreateLogger($"Vision[{request.DisplayName}]");
        _logger.LogInformation("Detection pipeline starting for camera {Camera} (id {CameraId}).",
            request.DisplayName, request.CameraId);

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

        _sourceWidth = request.Width;
        _sourceHeight = request.Height;

        int captureWidth, captureHeight;
        bool gpuPreprocessing;
        LetterboxGeometry? letterboxGeometry;

        if (aspectMode == AspectMode.Slice)
        {
            // See this class's own doc comment for why Slice mode has no single square
            // InferenceProfile and always forces GPU preprocessing regardless of the
            // Detection.GpuPreprocessing setting's own value (there is no CPU fallback for it).
            // _profile becomes the shared per-slice *identity* decode profile every slice's raw
            // output goes through — degenerate (Stretch, network-size square), the same instance
            // for every slice, never a per-camera transform.
            _sliceLayout = SliceLayout.Create(request.Width, request.Height, networkSize);
            _profile = InferenceProfile.Create(networkSize, networkSize, AspectMode.Stretch, networkSize);
            captureWidth = _sliceLayout.CaptureWidth;
            captureHeight = _sliceLayout.CaptureHeight;
            gpuPreprocessing = true;
            letterboxGeometry = null; // a plain scale to CaptureWidth x CaptureHeight, no pad at all

            // Nothing logged the resolved geometry before, which made every question about slicing
            // ("is anything actually landing in slice 1?", "how wide can an object be before it's
            // clipped in both?") unanswerable from a deployment log. Information rather than Debug
            // for the same reason the cadence line is: the node's own log level is Information.
            _logger.LogInformation(
                "Slice layout for camera {Camera}: source {SourceW}x{SourceH} -> capture {CapW}x{CapH}, " +
                "{Count} slice(s) of {Net} px squares at origin(s) {Origins} along the {Axis} axis, " +
                "overlap {Overlap} px ({OverlapPct:P0} of a slice). An object wider than the overlap is " +
                "clipped in every slice containing it and depends on the seam merge to be reported whole.",
                request.DisplayName, request.Width, request.Height,
                _sliceLayout.CaptureWidth, _sliceLayout.CaptureHeight,
                _sliceLayout.Slices.Count, networkSize,
                string.Join(", ", _sliceLayout.Origins), _sliceLayout.IsLandscape ? "X" : "Y",
                _sliceLayout.MinOverlap, _sliceLayout.MinOverlap / (double)networkSize);
        }
        else
        {
            _profile = InferenceProfile.Create(request.Width, request.Height, aspectMode, networkSize);
            captureWidth = _profile.NetworkWidth;
            captureHeight = _profile.NetworkHeight;

            // Pass 4a: with GPU preprocessing, ffmpeg emits packed nv12 (W*H*3/2 bytes) and the
            // engine's merged head does the colour convert + normalize on the accelerator;
            // otherwise BGRA (W*H*4) and the engine packs it on the CPU. Forced off for YOLOX —
            // OnnxPreprocessHead's non-sliced head is D-FINE-shaped (/255 RGB); a plain (non-Slice)
            // YOLOX variant was never needed once slicing shipped its own.
            gpuPreprocessing = request.GpuPreprocessing && modelFamily == DetectionModelFamily.DFine;
            letterboxGeometry = aspectMode == AspectMode.Letterbox
                ? new LetterboxGeometry(_profile.ScaledWidth, _profile.ScaledHeight, _profile.PadLeft, _profile.PadTop)
                : null;
        }

        _frameIsNv12 = gpuPreprocessing;
        var frameBytes = gpuPreprocessing
            ? captureWidth * captureHeight * 3 / 2
            : captureWidth * captureHeight * 4;

        _slot = new LatestFrameSlot(frameBytes);
        _session = new VisionSession(
            new VisionSessionOptions(resolvedFfmpegPath, request.RtspUri, captureWidth, captureHeight,
                request.HardwareAcceleration,
                Letterbox: letterboxGeometry,
                Nv12Output: gpuPreprocessing,
                FpsCap: request.DecodeFpsCap),
            _slot, loggerFactory.CreateLogger<VisionSession>());
        _session.InputResolutionDetected += OnInputResolutionDetected;

        // Only the options are built here — the engine itself is constructed by InferenceLoopAsync
        // (see the _engine field's own comment for why it must not happen on the /start thread).
        _modelFamily = modelFamily;
        _dfineWeights = dfineWeights;
        _yoloXSize = yoloXSize;
        _loggerFactory = loggerFactory;

        // D-FINE (a DETR/transformer) is fragile under the TensorRT builder in a way YOLOX (the CNN
        // TensorRT was validated against) is not — FP16 activation overflow yields NaN/Inf logits
        // that DFineDecoder silently drops as zero detections. Detection.DFineTensorRtMode (resolved
        // in CameraPipelineManager: the server-pushed value, else this machine's own local setting)
        // gates it for this family only; every other family follows EnableTensorRt/TensorRtPrecision
        // as-is. FP16 additionally runs the mixed-precision *.fp16.onnx model (decoder kept in FP32 —
        // resolvedModelPath already points at it) and turns on trt_layer_norm_fp32_fallback as a
        // second line of defence.
        var (enableTensorRt, tensorRtPrecision, layerNormFp32Fallback) =
            (serviceOptions.EnableTensorRt, serviceOptions.TensorRtPrecision, false);
        if (modelFamily == DetectionModelFamily.DFine)
        {
            (enableTensorRt, tensorRtPrecision, layerNormFp32Fallback) = dfineTensorRtMode.Trim().ToLowerInvariant() switch
            {
                "fp16" => (serviceOptions.EnableTensorRt, "FP16", true),
                "fp32" => (serviceOptions.EnableTensorRt, "FP32", false),
                _ => (false, serviceOptions.TensorRtPrecision, false), // "off" and anything unrecognized
            };
            _logger.LogInformation(
                "D-FINE TensorRT mode: {Mode} -> TensorRT {State}{Precision}{Fallback} for this camera's detection engine.",
                dfineTensorRtMode,
                enableTensorRt ? "enabled" : "disabled (plain CUDA)",
                enableTensorRt ? $" at {tensorRtPrecision}" : string.Empty,
                enableTensorRt && layerNormFp32Fallback ? " (LayerNorm kept in FP32)" : string.Empty);
        }

        _engineOptions = new EngineOptions
        {
            ModelPath = resolvedModelPath,
            GpuId = serviceOptions.GpuId,
            CudnnPath = serviceOptions.CudnnPath,
            EnableTensorRt = enableTensorRt,
            TensorRtPrecision = tensorRtPrecision,
            TensorRtLayerNormFp32Fallback = layerNormFp32Fallback,
            TensorRtEngineCachePath = serviceOptions.TensorRtEngineCachePath,
            TensorRtLibPath = serviceOptions.TensorRtLibPath,
            TensorRtMaxWorkspaceBytes = serviceOptions.TensorRtMaxWorkspaceBytes,
            TensorRtBuilderOptimizationLevel = serviceOptions.TensorRtBuilderOptimizationLevel,
            OpenVinoDeviceType = serviceOptions.OpenVinoDeviceType,
            GpuPreprocessing = gpuPreprocessing,
        };

        // Set here as well as inside the engine (which recomputes the identical string from the
        // identical inputs via the same helper) purely so EngineBuildGate can probe the cache for
        // *this* variant before any engine exists. If the two ever disagreed the only casualty is a
        // wrong warm/cold log line, never a wrong engine. Applied as a second step so BatchSize comes
        // from the options actually built above rather than a copy of its default.
        _engineOptions = _engineOptions with
        {
            TensorRtCacheKey = OrtSessionFactory.TensorRtCacheKeyFor(
                resolvedModelPath, _profile, _sliceLayout, _engineOptions.BatchSize, gpuPreprocessing),
        };

        // Per-family tracker tuning, anchored to this camera's own detection confidence — see
        // ByteTrackOptions.ForFamily for why the tracker's gates must follow the configured
        // confidence rather than sit at fixed values above it.
        var trackerOptions = ByteTrackOptions.ForFamily(modelFamily, request.Confidence);
        _trackerNewTrackThreshold = trackerOptions.HighThreshold;
        _tracker = new ByteTracker(trackerOptions);

        _runTask = RunAsync(_cts.Token);
    }

    /// <summary>Set when the deferred engine build failed, which leaves this pipeline capturing but
    /// never inferring. Because the build now happens after /start has already returned 200, this is
    /// how the failure reaches Node at all — CameraPipelineManager omits a failed pipeline from the
    /// watched-camera list, and NodeWorker's reconcile re-issues a start on a later tick.
    ///
    /// Volatile because it is latched on the inference loop's own thread and read from a Kestrel
    /// request thread serving GET /cameras.</summary>
    public bool EngineBuildFailed => _engineBuildFailed;
    private volatile bool _engineBuildFailed;

    /// <summary>What to call this camera in a log line — see VisionStartCameraRequest.DisplayName.
    /// Exposed so CameraPipelineManager can name a camera it is disposing, where only the id is in
    /// hand (VisionStopCameraRequest carries nothing else).</summary>
    public string DisplayName => _request.DisplayName;

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

        try
        {
            await Task.WhenAll(sessionTask, inferenceTask, checkpointTask, reportFlushTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Camera detection pipeline for {Camera} (id {CameraId}) failed.", _request.DisplayName, _request.CameraId);
        }
    }

    private async Task InferenceLoopAsync(CancellationToken ct)
    {
        // Build the engine before the first frame rather than in the constructor. Task.Run because
        // construction is a long synchronous native call (a TensorRT engine build is minutes on a
        // cold cache) and this method is first entered on whichever thread RunAsync was started
        // from — which, before this moved, was the /start request's own Kestrel thread.
        IDetectionEngine engine;
        try
        {
            engine = await Task.Run(() => EngineBuildGate.Build(
                _request.DisplayName, _engineOptions,
                () => DetectionEngineFactory.Create(_modelFamily, _dfineWeights, _yoloXSize,
                    _engineOptions, _profile, _loggerFactory, _sliceLayout),
                _logger, ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            // /start already returned 200 by this point, so there is no response left to fail — this
            // log and the pipeline going quiet are the whole signal. Node notices via the watched-
            // camera reconcile (GET /cameras) and re-issues a start on a later tick.
            _logger.LogError(ex,
                "Failed to build the detection engine for camera {Camera} (id {CameraId}) — this camera will not be watched. {Detail}",
                _request.DisplayName, _request.CameraId, Flatten(ex));
            _engineBuildFailed = true;
            await _cts.CancelAsync();
            return;
        }

        _engine = engine;

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
                detections = _sliceLayout is { } layout
                    ? MergeSliceDetections(layout, ((ISlicedDetectionEngine)engine).DetectSliced(frame, _request.Confidence))
                    : engine.Detect(frame, _request.Confidence, _request.Iou);
            }
            catch (Exception ex)
            {
                // See _cadenceInferenceFailures' own comment: the first failure logs in full, the
                // rest are counted onto the cadence line so a permanently broken engine doesn't
                // drown the log it would be diagnosed from.
                _cadenceInferenceFailures++;
                if (!_loggedInferenceFailure)
                {
                    _loggedInferenceFailure = true;
                    _logger.LogWarning(ex,
                        "Inference failed on one frame — skipping it. Further failures on this camera are counted " +
                        "on the detection cadence line rather than logged individually.");
                }
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
            _snapshottedTrackIds.RemoveWhere(id => !activeTrackIds.Contains(id));

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

                // _sourceWidth/_sourceHeight (this camera's own real dims), not frame.Width/Height —
                // as of pass 1, detection.BoundingBox is reported in that same source-pixel space
                // (see DFineDecoder.Decode's own doc comment) for Letterbox/Stretch, and pass 4's
                // MergeSliceDetections below already remaps every slice's own local box into it
                // before the tracker (and therefore this loop) ever sees it. Normalizing against the
                // wrong dimensions would silently misplace every box on a non-Stretch camera.
                var observation = _movement.Observe(trackId, detection.BoundingBox, detection.Confidence,
                    _sourceWidth, _sourceHeight, now);

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
                    UpdateBestFrameForLabel(label, detection, _sourceWidth, _sourceHeight, now);

                    movingThisFrame.Add((label, detection));
                    // Pass G: a track we've never snapshotted starting to move is what triggers this
                    // frame's first Sub-frame snapshot after this loop. The progressive upgrade on
                    // top of that first crop is decided after the loop from the whole frame's
                    // CompositeFrameScore, not per track — see the trigger block below.
                    if (_snapshottedTrackIds.Add(trackId)) newMovingTrack = true;
                }

                var hysteresis = GetOrCreateHysteresis(label);
                if (hysteresis.Observe(now, motionPresent, detection.Confidence) is { } closed)
                {
                    EnqueueReport(closed, label, category);
                }

                liveBoxes.Add(new VisionLiveDetectionBox(
                    trackId, rawCategory, rawLabel, observation.State.ToString(),
                    detection.BoundingBox.Left / (double)_sourceWidth,
                    detection.BoundingBox.Top / (double)_sourceHeight,
                    detection.BoundingBox.Width / (double)_sourceWidth,
                    detection.BoundingBox.Height / (double)_sourceHeight,
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

            // Pass G / progressive best snapshot: how well this frame captures *every* moving object
            // at once — the count plus their cumulative confidence*area. Boxes are in this camera's
            // source-pixel space here (same as everywhere else in this loop). Built with a plain loop
            // rather than CompositeFrameScore.ForFrame (which the tests use) to keep this hot path
            // allocation-free.
            var cumulativeSnapshotScore = 0.0;
            foreach (var (_, det) in movingThisFrame)
            {
                var mb = det.BoundingBox;
                var mArea = Math.Clamp(
                    (mb.Width / (double)_sourceWidth) * (mb.Height / (double)_sourceHeight), 0.0, 1.0);
                cumulativeSnapshotScore += MovementClassifier.Score(det.Confidence, mArea);
            }
            var frameScore = new CompositeFrameScore(movingThisFrame.Count, cumulativeSnapshotScore);

            // Snapshot everything moving straight from this exact frame when a new track started
            // moving (its first crop), or this frame frames the moving set better than whatever is
            // staged — more objects, or the same number more clearly. Per-camera cadence gate still
            // caps a burst at one crop per second.
            if ((newMovingTrack || frameScore.BeatsRetained(_stagedSnapshotScore, SnapshotImprovementMargin))
                && now - _lastSubSnapshotUtc >= SubSnapshotMinGap && movingThisFrame.Count > 0)
            {
                _lastSubSnapshotUtc = now;
                TrySubFrameSnapshot(frame, now, movingThisFrame, frameScore);
            }
        }
    }

    // Local-pixel coordinates within this margin of a slice's own edge, along the axis
    // SliceLayout.Slices overlap on, count as "the model's own view of this object was itself cut
    // off by the tile boundary" — see SliceMerge.Candidate.TouchesSliceEdge's own doc comment. A
    // couple of pixels of tolerance for rounding, not zero: DFineDecoder/YoloXDecoder both round to
    // the nearest integer pixel when clamping a box to the network size.
    private const int SliceEdgeMarginPx = 2;

    /// <summary>Detection/hardware-acceleration overhaul, pass 4: maps every slice's own local-pixel
    /// detections into this camera's global source-pixel space (<see cref="SliceLayout.MapSliceBoxToSource"/>,
    /// then <see cref="_sourceWidth"/>/<see cref="_sourceHeight"/>) and merges the pooled result with
    /// <see cref="SliceMerge"/>, so the tracker below sees exactly the same shape it always has —
    /// one flat <c>List&lt;ObjectDetection&gt;</c> in this camera's own real pixel space — regardless
    /// of how many slices actually produced it.</summary>
    private List<YoloDotNet.Models.ObjectDetection> MergeSliceDetections(SliceLayout layout, List<List<YoloDotNet.Models.ObjectDetection>> perSlice)
    {
        _cadenceSliceDetections ??= new int[perSlice.Count];

        var candidates = new List<SliceMerge.Candidate>();
        for (var i = 0; i < perSlice.Count; i++)
        {
            if (i < _cadenceSliceDetections.Length) _cadenceSliceDetections[i] += perSlice[i].Count;

            foreach (var d in perSlice[i])
            {
                var box = d.BoundingBox; // local pixel space, 0..NetworkSize (this slice's own square)
                var touchesEdge = layout.IsLandscape
                    ? box.Left <= SliceEdgeMarginPx || box.Right >= layout.NetworkSize - SliceEdgeMarginPx
                    : box.Top <= SliceEdgeMarginPx || box.Bottom >= layout.NetworkSize - SliceEdgeMarginPx;

                var (nx0, ny0, nx1, ny1) = layout.MapSliceBoxToSource(i,
                    box.Left / (double)layout.NetworkSize, box.Top / (double)layout.NetworkSize,
                    box.Right / (double)layout.NetworkSize, box.Bottom / (double)layout.NetworkSize);

                var left = (int)Math.Round(Math.Clamp(nx0 * _sourceWidth, 0, _sourceWidth));
                var top = (int)Math.Round(Math.Clamp(ny0 * _sourceHeight, 0, _sourceHeight));
                var right = (int)Math.Round(Math.Clamp(nx1 * _sourceWidth, 0, _sourceWidth));
                var bottom = (int)Math.Round(Math.Clamp(ny1 * _sourceHeight, 0, _sourceHeight));
                if (right <= left || bottom <= top) continue; // degenerate — nothing to report

                candidates.Add(new SliceMerge.Candidate(new SKRectI(left, top, right, bottom),
                    d.Confidence, d.Label?.Name ?? "object", touchesEdge));
            }
        }

        var merged = SliceMerge.Merge(candidates, out var mergeStats);
        _cadenceIouAbsorbed += mergeStats.IouAbsorbed;
        _cadenceSeamAbsorbed += mergeStats.SeamAbsorbed;

        var results = new List<YoloDotNet.Models.ObjectDetection>(merged.Count);
        foreach (var m in merged)
        {
            if (SpansMoreThanOneSlice(layout, m.Box)) _cadenceSpanningMerges++;

            results.Add(new YoloDotNet.Models.ObjectDetection
            {
                Label = new YoloDotNet.Models.LabelModel { Index = 0, Name = m.Label },
                Confidence = m.Confidence,
                BoundingBox = m.Box,
                Tail = [],
            });
        }
        return results;
    }

    /// <summary>True when <paramref name="box"/> (this camera's own source-pixel space, post-merge)
    /// is too wide along the slicing axis to fit inside any single slice — so reporting it whole
    /// required at least two slices' detections to be reunited. Counting these is the direct answer
    /// to "does anything crossing a seam ever get reported?", which no existing counter could give:
    /// a spanning object that never merges simply doesn't appear anywhere, and absence is not
    /// distinguishable from the model not seeing it.</summary>
    private bool SpansMoreThanOneSlice(SliceLayout layout, SKRectI box)
    {
        // Source pixels -> capture pixels along the slicing axis only; the short axis is a whole
        // slice edge by construction and can never be the reason something spans.
        var (lo, hi) = layout.IsLandscape
            ? (box.Left / (double)_sourceWidth * layout.CaptureWidth, box.Right / (double)_sourceWidth * layout.CaptureWidth)
            : (box.Top / (double)_sourceHeight * layout.CaptureHeight, box.Bottom / (double)_sourceHeight * layout.CaptureHeight);

        foreach (var origin in layout.Origins)
        {
            if (lo >= origin && hi <= origin + layout.NetworkSize) return false;
        }
        return true;
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

        // Slice mode only — see _cadenceSliceDetections' own comment. Appended to the existing line
        // rather than logged separately so one window's numbers stay together in the log.
        var sliceSummary = string.Empty;
        if (_sliceLayout is not null && _cadenceSliceDetections is { } perSlice)
        {
            sliceSummary =
                $" Slices: raw detection(s) per slice [{string.Join(", ", perSlice)}], merged {_cadenceIouAbsorbed} by " +
                $"overlap and {_cadenceSeamAbsorbed} across a seam, {_cadenceSpanningMerges} reported box(es) too wide " +
                $"for any one slice.";
            Array.Clear(perSlice);
            _cadenceIouAbsorbed = 0;
            _cadenceSeamAbsorbed = 0;
            _cadenceSpanningMerges = 0;
        }

        var failures = _cadenceInferenceFailures;
        _cadenceInferenceFailures = 0;
        var failureSummary = failures == 0
            ? string.Empty
            : $" {failures} frame(s) failed inference in this window (first one logged in full above).";

        _logger.LogInformation(
            "Camera {Camera} detection cadence over {Seconds:F0}s: capture {CaptureFps:F1} fps, " +
            "inference {InferenceFps:F1} fps, {Dropped} frame(s) dropped ({DropPercent:F0}%), " +
            "last inference {InferenceMs:F1} ms. Peak {PeakDetections} detection(s)/frame -> " +
            "{PeakTracked} tracked, best raw score {BestScore:F2} (tracker needs {NewTrackThreshold:F2} " +
            "to start a track). Live boxes now: {LiveBoxes}. Process-wide over the same window: " +
            "{Gen2} gen2 collection(s), {AllocatedMbPerSec:F0} MB/s allocated.{SliceSummary}{FailureSummary}",
            _request.DisplayName, seconds, deltaPublished / seconds, deltaConsumed / seconds,
            deltaPublished - deltaConsumed,
            deltaPublished == 0 ? 0 : (deltaPublished - deltaConsumed) * 100.0 / deltaPublished,
            _engine?.LastInferenceMilliseconds ?? 0,
            peakDetections, peakTracked, bestScore, _trackerNewTrackThreshold, liveSummary,
            deltaGen2, deltaAllocated / seconds / (1024.0 * 1024.0), sliceSummary, failureSummary);
    }

    /// <summary>Pass G: crops one JPEG from the frame the model just ran on, covering the union of
    /// every currently-Moving box, and becomes the authoritative snapshot for every one of those
    /// labels' spans (progressive best snapshot — see _stagedSnapshotScore's own doc comment). The
    /// boxes came out of these exact pixels, so — unlike a later timestamp lookup into recorded
    /// footage — the objects cannot have moved off the crop. ~150-300 px at the network buffer size.
    /// <paramref name="frameScore"/> is this frame's CompositeFrameScore, already computed by the
    /// caller for the trigger decision and stored as the new staged score once the crop is posted.</summary>
    private void TrySubFrameSnapshot(byte[] frame, DateTime nowUtc,
        List<(string Label, YoloDotNet.Models.ObjectDetection Detection)> moving, CompositeFrameScore frameScore)
    {
        try
        {
            // Slice mode's frame is _sliceLayout's own non-square CaptureWidth x CaptureHeight
            // buffer, not this method's assumed square NetworkWidth x NetworkHeight one, and its
            // boxes (post-merge) are already in _sourceWidth x _sourceHeight space rather than the
            // network-buffer-pixel space _profile.NormalizedSourceToNetworkPixels below undoes into
            // — genuinely different geometry, not just a parameter swap, hence its own method.
            if (_sliceLayout is { } layout)
            {
                TrySliceSnapshot(layout, frame, nowUtc, moving, frameScore);
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
            if (crop.Width < 2 || crop.Height < 2) return;

            // BGRA path takes the final (margined, content-clamped) rect; the nv12 path reuses
            // Nv12Ops.CropToJpeg, which applies its own margin + frame clamp, so it gets the raw
            // union instead. nv12 only happens with GpuPreprocessing on (D-FINE only, off by default).
            // No whole-frame guard: covering every moving object is the point even when they're at
            // opposite edges — the union of the real detection boxes plus a 12% margin, clamped to
            // the content rect, is never worse than a full-frame grab.
            var jpeg = _frameIsNv12
                ? Nv12Ops.CropToJpeg(frame, _profile.NetworkWidth, _profile.NetworkHeight, ClampTo(unionRect, content), EagerCropJpegQuality)
                : BgraOps.CropRectToJpeg(frame, _profile.NetworkWidth, _profile.NetworkHeight, crop, EagerCropJpegQuality);
            if (jpeg is null) return;

            _ = PostEagerCropAsync(nowUtc, jpeg);
            PromoteAuthoritativeBest(moving, nowUtc, frameScore);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sub-frame snapshot failed for camera {Camera} — that span falls back to the segment-seek crop.", _request.DisplayName);
        }
    }

    /// <summary>Detection/hardware-acceleration overhaul, pass 4: <see cref="TrySubFrameSnapshot"/>'s
    /// own logic, adapted for Slice mode's different geometry — <paramref name="frame"/> is
    /// <paramref name="layout"/>'s own non-square CaptureWidth×CaptureHeight nv12 buffer (always
    /// nv12 — Slice mode has no BGRA path at all, see this class's own doc comment), and
    /// <paramref name="moving"/>'s boxes are already in this camera's <see cref="_sourceWidth"/>×
    /// <see cref="_sourceHeight"/> space (post-<see cref="MergeSliceDetections"/>), not a
    /// letterbox/stretch network buffer's — so the crop rescales uniformly from source-pixel space
    /// into capture-pixel space (<see cref="SliceLayout"/>'s own uniform, unpadded scale) instead of
    /// undoing a letterbox pad.</summary>
    private void TrySliceSnapshot(SliceLayout layout, byte[] frame, DateTime nowUtc,
        List<(string Label, YoloDotNet.Models.ObjectDetection Detection)> moving, CompositeFrameScore frameScore)
    {
        var content = new SKRectI(0, 0, layout.CaptureWidth, layout.CaptureHeight);
        var scaleX = layout.CaptureWidth / (double)_sourceWidth;
        var scaleY = layout.CaptureHeight / (double)_sourceHeight;

        SKRectI ToCapturePixels(SKRectI b) => new(
            (int)Math.Round(b.Left * scaleX), (int)Math.Round(b.Top * scaleY),
            (int)Math.Round(b.Right * scaleX), (int)Math.Round(b.Bottom * scaleY));

        SKRectI? union = null;
        foreach (var (_, det) in moving)
        {
            var px = ToCapturePixels(det.BoundingBox);
            union = union is { } u
                ? new SKRectI(Math.Min(u.Left, px.Left), Math.Min(u.Top, px.Top),
                    Math.Max(u.Right, px.Right), Math.Max(u.Bottom, px.Bottom))
                : px;
        }
        if (union is not { } unionRect || unionRect.Width < 2 || unionRect.Height < 2) return;

        // Always nv12 — Slice mode has no BGRA path (mandatory GPU preprocessing, see this class's
        // own doc comment). Nv12Ops.CropToJpeg applies its own margin + frame clamp, so it gets the
        // raw union directly (no separate ComputeCropRect step, unlike the BGRA branch of
        // TrySubFrameSnapshot). No whole-frame guard — see TrySubFrameSnapshot for why covering every
        // moving object wins over a tighter single-object crop.
        var jpeg = Nv12Ops.CropToJpeg(frame, layout.CaptureWidth, layout.CaptureHeight, ClampTo(unionRect, content), EagerCropJpegQuality);
        if (jpeg is null) return;

        _ = PostEagerCropAsync(nowUtc, jpeg);
        PromoteAuthoritativeBest(moving, nowUtc, frameScore);
    }

    /// <summary>After an eager crop is posted for <paramref name="nowUtc"/>, makes that frame the
    /// authoritative snapshot for every label with a moving detection in it — all their spans point
    /// at the one composite image, and each carries a box from this same frame so an overlay lines
    /// up. Unconditional (not "only if this label's own view improved"): the whole trigger already
    /// decided this frame frames the moving set better than what was staged. When a label has
    /// several moving instances this frame, its largest confidence*area box represents it. Boxes
    /// stored normalized 0-1 in _sourceWidth/_sourceHeight space, matching every other BestFrame.</summary>
    private void PromoteAuthoritativeBest(
        List<(string Label, YoloDotNet.Models.ObjectDetection Detection)> moving, DateTime nowUtc, CompositeFrameScore frameScore)
    {
        double NormArea(SKRectI b) => Math.Clamp(
            (b.Width / (double)_sourceWidth) * (b.Height / (double)_sourceHeight), 0.0, 1.0);

        foreach (var group in moving.GroupBy(m => m.Label, StringComparer.OrdinalIgnoreCase))
        {
            var det = group
                .OrderByDescending(m => MovementClassifier.Score(m.Detection.Confidence, NormArea(m.Detection.BoundingBox)))
                .First().Detection;
            var b = det.BoundingBox;
            _authoritativeBestByLabel[group.Key] = new BestFrame(nowUtc,
                b.Left / (double)_sourceWidth, b.Top / (double)_sourceHeight,
                b.Width / (double)_sourceWidth, b.Height / (double)_sourceHeight,
                det.Confidence);
        }

        _stagedSnapshotScore = frameScore;
    }

    /// <summary>Intersects a rect with <paramref name="bounds"/> — the codebase hand-rolls rect math
    /// rather than depend on a particular SkiaSharp helper surface.</summary>
    private static SKRectI ClampTo(SKRectI r, SKRectI bounds) => new(
        Math.Max(r.Left, bounds.Left), Math.Max(r.Top, bounds.Top),
        Math.Min(r.Right, bounds.Right), Math.Min(r.Bottom, bounds.Bottom));

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
        // A1: a pass G eager crop's own box, if this span got one, is authoritative for the
        // snapshot — it has a matching eager crop already staged on the node. Otherwise fall back to
        // the continuous pass's own best frame: prefer the normal-sized candidate; an oversized one
        // (see LabelBestFrameTracker's own doc comment) only stands in when nothing normal-sized was
        // ever seen for this span.
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
            // Once every co-detected label's span has closed, nothing on screen is described by the
            // staged composite score any more — drop it so a later, separate burst of activity runs
            // its own fresh contest instead of being held to a bar (e.g. a two-object frame) it may
            // never clear. Kept until then, so a track briefly lost mid-span and re-acquired doesn't
            // discard the good crop already staged for it.
            if (_authoritativeBestByLabel.Count == 0) _stagedSnapshotScore = default;
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
                _logger.LogWarning(ex, "Failed to report a detection span for camera {Camera} to Node — will retry.", _request.DisplayName);
                _pendingReports.Enqueue(item);
            }
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
            "Camera {Camera} (id {CameraId}): AI detection was set up for a {AssumedWidth}x{AssumedHeight} stream but " +
            "ffmpeg is actually decoding {ActualWidth}x{ActualHeight}. The model is running on a distorted " +
            "frame and snapshot crops will look stretched — set this camera's AI detection orientation to " +
            "{Orientation} (Cameras > Edit, or the deployment default on Admin > Settings > Detection).",
            _request.DisplayName, _request.CameraId, _request.Width, _request.Height, width, height,
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
            _logger.LogDebug(ex, "Could not ship an eager snapshot crop for camera {Camera} — that span will fall back to the segment-seek crop.", _request.DisplayName);
        }
    }

    /// <summary>Flattens an exception chain to its messages only — same reasoning as Program's own
    /// Describe: what makes an engine-load failure diagnosable is almost always the innermost message
    /// (ONNX Runtime's "which depends on X.dll which is missing" text), and a build failure now lands
    /// only in this log rather than in a /start response body.</summary>
    private static string Flatten(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message)) messages.Add(current.Message);
        }
        return string.Join(" -> ", messages);
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
        _engine?.Dispose();
    }
}
