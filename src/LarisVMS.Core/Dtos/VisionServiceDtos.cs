namespace LarisVMS.Core.Dtos;

// Wire DTOs for the localhost-only control channel between LarisVMS.Node and its sibling
// LarisVMS.Vision.Service process (object detection plan decisions 2/3). Shared via Core, the same
// way NodeDtos.cs shares the Node<->Web protocol, so neither side needs to reference the other:
// LarisVMS.Node references LarisVMS.Core directly, and LarisVMS.Vision.Service gets it transitively
// through LarisVMS.Vision — LarisVMS.Node never references LarisVMS.Vision itself (see decision 3's
// "never the reverse" reasoning; the whole point of the sibling-process split is keeping Node's own
// publish footprint free of the GPU/ONNX Runtime native dependencies Vision carries).
//
// Both processes are on the same machine and the channel is bound to localhost only — this is not
// a public API surface, and carries no bearer-secret auth of its own (the OS's own loopback
// isolation is the trust boundary, same reasoning ffmpeg's own stdio pipes already rely on
// throughout this codebase).

/// <summary>Node -&gt; Vision Service: start (or replace, if this camera is already running with a
/// different configuration) watching one camera. Carries everything Vision Service needs for this
/// camera — RTSP URI, decode settings, detection thresholds, and where to report back — rather than
/// Vision Service reading any of it from its own local config, since all of it (unlike the model
/// path and accelerator, which really are facts about this specific machine) is either per-camera
/// or resolved centrally by the server.</summary>
public record VisionStartCameraRequest(
    Guid CameraId,
    /// <summary>The camera's Sub stream RTSP URI, already credential-injected — the same shape
    /// ReconcileMotion/ReconcileLiveSub already build for MotionSession/SubLiveSession.</summary>
    string RtspUri,
    /// <summary>Detection/hardware-acceleration overhaul, pass 1: this camera's own real Sub-stream
    /// resolution (from CameraStream.Width/Height, the ffmpeg-probed ground truth — see
    /// RecordingSession.TryParseVideoStreamLine's own doc comment for why that beats ONVIF's
    /// advertised value), falling back to 1280x720 if not yet probed. Before this pass these were a
    /// single fixed *decode target* shared by every camera regardless of its own aspect ratio
    /// (NodeConfigResponse.AiDetectionWidth/Height, now retired) — squashing a portrait or panoramic
    /// camera into whatever that global resolution's own aspect happened to be, before D-FINE's own
    /// 640x640 stretch squashed it a second time. Now purely an input to
    /// LarisVMS.Vision.Inference.InferenceProfile, which derives the actual ffmpeg decode target
    /// (always exactly its own NetworkWidth/NetworkHeight) from these plus AspectMode below.
    /// The watch stream is never ffmpeg-probed node-side, so this can start out wrong (a stale ONVIF
    /// value, or the Main-stream aspect as a stand-in, or the 1280x720 fallback) — a camera that
    /// misreports its shape outright is corrected by the operator-set AiDetection.Orientation the
    /// node applies before building this request (see LarisVMS.Node.DetectionOrientation), and
    /// VisionSession warns when what ffmpeg actually decodes still disagrees.</summary>
    int Width,
    int Height,
    /// <summary>ffmpeg -hwaccel value for VisionSession's own GPU-hybrid decode, e.g. "cuda" — see
    /// VisionSession.StartFfmpeg. Independent of which YoloDotNet execution provider this build of
    /// Vision Service was compiled for (Accel/ACCEL_* — decode acceleration and inference
    /// acceleration are two separate GPU usages).</summary>
    string? HardwareAcceleration,
    double Confidence,
    double Iou,
    /// <summary>Object detection plan decision 7's ReportIdleDetections — whether an Idle track
    /// ever reaches Node's /detections endpoint at all. Never affects the live snapshot
    /// (GET /cameras/{id}/detections), which always includes every currently-tracked object
    /// regardless of this setting — see decision 6's Moving/Idle live-overlay toggles.</summary>
    bool ReportIdleDetections,
    /// <summary>How long (MotionHysteresis's endAfter) a label's span stays open after motion stops
    /// before actually closing — see NodeConfigResponse.AiIdleTimeoutSeconds's own doc comment for
    /// why this needs to be non-zero.</summary>
    int IdleTimeoutSeconds,
    /// <summary>The DetectionModelFamily enum name (e.g. "DFine") this camera's pipeline should
    /// load — always already resolved to a concrete family by the time it reaches here (Node's own
    /// DetectionModelSelection.Choose runs Auto/accelerator resolution before this request is
    /// built; Vision Service never sees "Auto").</summary>
    string ModelFamily,
    /// <summary>The DFineWeights enum name (e.g. "Obj2Coco") — only meaningful when ModelFamily is
    /// "DFine", carried unconditionally the same way HardwareAcceleration is always present even
    /// though only some accelerators use every one of its fields.</summary>
    string DFineWeights,
    /// <summary>Where this Vision Service instance should POST closed/checkpointed detection spans
    /// back to, e.g. "http://127.0.0.1:{nodePort}" — Node's own localhost-only control port. Told
    /// to Vision Service rather than assumed/hardcoded so neither side has a second place to keep a
    /// port number in sync.</summary>
    string NodeCallbackBaseUrl,
    /// <summary>Detection/hardware-acceleration overhaul, pass 1: the AspectMode enum name (e.g.
    /// "Letterbox") — node-scoped like ModelFamily/DFineWeights (Setting + SettingOverride(Scope.Node),
    /// resolved on NodeConfigResponse.AspectMode before this request is built), not per-camera: one
    /// choice governs how every camera on a node fits its own aspect into D-FINE's square input.
    /// Defaults to "Letterbox" so an older, not-yet-updated node build's deserialization (were this
    /// ever read node-side, which it isn't — Vision Service parses it directly) lands on the safer of
    /// the two implemented values rather than today's stretch-and-distort behavior.</summary>
    string AspectMode = "Letterbox",
    /// <summary>Detection/hardware-acceleration overhaul, pass 3b: opt-in (default off, deliberately
    /// — meaningfully more CPU/GPU work than the continuous Sub-stream pipeline alone) motion-guided
    /// native-scale re-detection against the Main stream. Global -> Node resolved setting, same
    /// scoping as AspectMode/ModelFamily above (one Vision Service process per node, so this is a
    /// per-node choice, not per-camera).</summary>
    bool EnableHighResReDetection = false,
    /// <summary>This camera's own real Main-stream resolution (CameraStream.Width/Height for the
    /// Main role, the same ffmpeg-probed-ground-truth source Width/Height above already uses for the
    /// Sub stream) — null until RecordingSession has actually probed it. Only meaningful when
    /// EnableHighResReDetection is true; CameraDetectionPipeline's own high-res loop stays
    /// permanently disabled for a camera until both are known.</summary>
    int? MainStreamWidth = null,
    int? MainStreamHeight = null,
    /// <summary>Detection.EnableVisionDebugImages — diagnostic-only, node-scoped like AspectMode
    /// above. When true, CameraDetectionPipeline writes one cropped JPEG per high-res re-detection
    /// trigger to logs\vision-debug\. Off by default; SaveDebugImage is a no-op unless this is set.</summary>
    bool EnableVisionDebugImages = false,
    /// <summary>Detection.GpuPreprocessing (pass 4a) — node-scoped. When true, ffmpeg emits packed
    /// nv12 and DFineEngine merges an nv12→normalized-tensor head into the model so colour conversion
    /// + normalize run on the accelerator instead of a CPU pixel loop. Vendor-neutral. Off by default.
    /// Ignored for a YOLOX pipeline (no YOLOX preprocess head yet — CameraDetectionPipeline forces
    /// BGRA frames + CPU pack).</summary>
    bool GpuPreprocessing = false,
    /// <summary>The YoloXSize enum name (Nano/Tiny/S/M/L/X) — only meaningful when ModelFamily is
    /// "YoloX"; selects which ONNX the Vision Service fetches (via the node's model proxy) and its
    /// square network input size (416 for Nano/Tiny, 640 otherwise). Appended so NodeWorker's
    /// positional construction stays stable; defaults "S".</summary>
    string YoloXSize = "S",
    /// <summary>Already-resolved <c>fps=</c> value for VisionSession's ffmpeg filter chain — Node
    /// computes it from Detection.MaxFps and the Sub stream's own probed rate, sending 0 whenever the
    /// stream is already at or below the cap (so a slow camera is never frame-*duplicated* up to the
    /// target). 0 = no fps filter.</summary>
    int DecodeFpsCap = 0,
    /// <summary>Detection.HiResSnapshots (Pass F) — node-scoped, opt-in, off by default. When true the
    /// Vision Service decodes the Sub stream at up to its native resolution (long edge capped to
    /// SnapshotImageCapture.MaxDimension) instead of the detector's network input size, downscales a
    /// copy per frame for inference, and crops the eager AI-detection snapshot from the larger buffer
    /// — sharper only where the Sub stream's own resolution exceeds the network size. Small extra CPU
    /// + memory per camera, no extra GPU. Forces GpuPreprocessing off for the camera while on (the
    /// merged nv12 head can't consume a capture-sized frame). Appended last so positional
    /// construction stays stable.</summary>
    bool HiResSnapshots = false);

/// <summary>Node -&gt; Vision Service: stop watching a camera (disabled, reassigned, or the node is
/// shutting down this camera's session).</summary>
public record VisionStopCameraRequest(Guid CameraId);

/// <summary>Node -&gt; Vision Service (POST /log-level): the deployment-wide minimum log level, pushed
/// whenever the Logging.Level setting changes so the sibling process follows it without a restart.</summary>
public record LogLevelRequest(string Level);

/// <summary>Vision Service -&gt; Node (POST /detections): one closed or checkpointed AI-detection
/// span. Deliberately shaped to map directly onto MotionSpanReportItem's own AI-detection fields —
/// NodeWorker just folds this straight into its existing _pendingMotionSpans/
/// FlushMotionSpansAsync pipeline, unchanged, the same way it already does for every other
/// detection source (ServerMotion, CameraEvent, Integration).</summary>
public record VisionDetectionReportItem(
    Guid CameraId,
    DateTime StartUtc,
    DateTime EndUtc,
    double Score,
    /// <summary>The resolved category name (CocoCategoryMap.Resolve's output, e.g. "Vehicle") —
    /// Vision Service never assigns a color or database Id; NodeService.RecordMotionSpansAsync's
    /// find-or-create is still the sole place a DetectedObjectCategory row is ever created.</summary>
    string DetectedObjectCategory,
    /// <summary>The specific raw class name (e.g. "car"), exactly as YoloDotNet reported it.</summary>
    string DetectedObjectLabel,
    DateTime? BestFrameAtUtc,
    double? BestBoxX,
    double? BestBoxY,
    double? BestBoxW,
    double? BestBoxH,
    double? BestBoxConfidence);

/// <summary>Vision Service -&gt; Node (POST /detections/crop): a snapshot image cropped from the
/// exact Main-stream frame a high-res re-detection ran against — the same frame the vision-debug
/// images come from, so it lines up with the detected object far better than a whole-second seek
/// into the recorded segment can. Node stages it by <see cref="AtUtc"/> ticks under
/// <c>cam-{id}/snapshots/hires/</c>; the /snapshot-image route promotes it into the span-keyed
/// snapshot cache on first view (so retention governs it like any other snapshot). Best-effort —
/// a lost crop just means that span falls back to the segment-seek crop.</summary>
public record VisionDetectionCropItem(Guid CameraId, DateTime AtUtc, byte[] Jpeg);

/// <summary>Node -&gt; Vision Service (GET /cameras/{cameraId}/detections) response: the live,
/// current-instant snapshot for the live-view box overlay (decision 6) — every object Vision
/// Service is currently tracking for this camera, Moving or Idle alike, regardless of
/// ReportIdleDetections (which only ever gates the /detections *report*, never this live read).
/// Deliberately carries no color: Vision Service has no database access at all, the same way Node
/// itself never touches SQL Server directly — LarisVMS.Web's own live-view proxy is what attaches
/// each category's color when it relays this feed on to a browser.</summary>
public record VisionLiveDetectionsResponse(Guid CameraId, DateTime AsOfUtc, List<VisionLiveDetectionBox> Boxes);

public record VisionLiveDetectionBox(
    int TrackId,
    string Category,
    string Label,
    /// <summary>"Moving" or "Idle" — MovementClassifier.MovementState.ToString().</summary>
    string MovementState,
    /// <summary>Normalized 0-1 against the resolution Vision Service is decoding this camera at
    /// (VisionStartCameraRequest.Width/Height) — the same coordinate space decision 6's live
    /// overlay already expects.</summary>
    double X, double Y, double W, double H,
    double Confidence);
