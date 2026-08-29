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
    /// (always exactly its own NetworkWidth/NetworkHeight) from these plus AspectMode below.</summary>
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
    string AspectMode = "Letterbox");

/// <summary>Node -&gt; Vision Service: stop watching a camera (disabled, reassigned, or the node is
/// shutting down this camera's session).</summary>
public record VisionStopCameraRequest(Guid CameraId);

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
