using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Dtos;

// Wire DTOs for the node control plane (POST /api/nodes/*). Shared via Core so LarisVMS.Node can
// serialize/deserialize them without referencing LarisVMS.Infrastructure or LarisVMS.Web.

public record NodeRegisterRequest(string RegistrationKey, string Hostname, string? Version, string? Platform);
public record NodeRegisterResponse(Guid NodeId, string Secret, string MediaSigningKey);

/// <summary>SentAtUtc is the node's own DateTime.UtcNow at the moment it builds this request — the
/// web server compares that against its own receive-time to measure this node's OS clock skew (see
/// Node.ClockSkewSeconds). Defaulted rather than required so an older node build (pre-dating this
/// field) still deserializes cleanly against a newer Web — it just never gets a skew measurement.
/// DetectedEncoders (M17) is null on an older node build the same way, and also on a newer one that
/// simply hasn't finished its first probe yet — see NodeWorker's own caching of the probe result.</summary>
/// <summary>DetectedAccelerators (object detection plan decision 2) mirrors DetectedEncoders exactly
/// — self-reported from the node's own AccelCapabilityProber, informational only (drives the Admin
/// UI's readout in Node.DetectedAcceleratorsJson); the node always acts on its own fresh local probe,
/// never a value round-tripped back from the server.</summary>
public record NodeHeartbeatRequest(string? Version, long? FreeBytes = null, long? TotalBytes = null, int? LivePort = null,
    DateTime? SentAtUtc = null, List<string>? DetectedEncoders = null, List<string>? DetectedAccelerators = null);

/// <summary>Recorder-node auto-update: a genuinely newer NodeBuildVersion exists for this node's
/// reported Platform (NodeVersionComparer.IsNewer), and NodeAutoUpdate.Enabled is on. DownloadUrl is
/// an absolute URL back to this same server's /api/nodes/download/{buildId} — reusing the node's own
/// Bearer nodeId:secret credentials, same auth as every other /api/nodes/* route (NodeAuthMiddleware).
/// Sha256 is what LarisVMS.Node.Update.UpdateService verifies the download against before applying
/// it; a mismatch aborts without touching the running binary.
///
/// VisionDownloadUrl/VisionSha256/VisionSizeBytes (object detection plan follow-up) are all null
/// together when the matching NodeBuildVersion row has no Vision Service binary registered (see that
/// entity's own doc comment) — UpdateService only attempts the second swap when all three are
/// present. Riding the same Version as the Node exe rather than carrying its own — LarisVMS.Vision.Service
/// is a build artifact of the same release, not independently versioned.</summary>
public record NodeUpdateInfoDto(string Version, string DownloadUrl, string Sha256, long SizeBytes,
    string? VisionDownloadUrl = null, string? VisionSha256 = null, long? VisionSizeBytes = null);

public record NodeHeartbeatResponse(int IntervalSeconds, NodeUpdateInfoDto? UpdateAvailable = null);

public record NodeConfigStreamDto(Guid StreamId, string Role, string RtspUri,
    string? Codec, int? Width, int? Height, bool HasAudio,
    /// <summary>The stream's probed frame rate (CameraStream.Fps), or null if not probed yet — used
    /// only to decide whether Detection.MaxFps's fps= filter would actually cap (vs pointlessly
    /// duplicating a stream that's already at or below the target).</summary>
    int? Fps = null);

/// <summary>M8/M18: ServerMotion, Ignore, and (as of M18) Privacy zones are sent here — CameraMotion
/// is the one left out, since nothing pushes it to a device yet (that push happens from LarisVMS.Web
/// over ONVIF in a follow-up pass), so there's no reason to hand a recorder node polygon data it
/// can't act on.</summary>
public record NodeConfigZoneDto(Guid ZoneId, string Kind, string PolygonJson, double Sensitivity);

/// <summary>M8 pass 8: one user-configured EventTagRule, handed to the node so CameraEventSession can
/// match incoming ONVIF notifications against it independently of the built-in motion classifier.
/// Only enabled rules for this camera are ever included. Color isn't needed here — that's purely a
/// web-tier/timeline-rendering concern the node has no reason to know about.</summary>
public record NodeConfigEventTagRuleDto(Guid Id, string StartTopic, string? StopTopic, bool DrivesRecording);

/// <summary>One ScheduleWindow, handed to the node for Recording.Mode=Schedule the same way Zones/
/// EventTagRules are — Days is DayOfWeekFlags.ToString() (e.g. "Monday, Wednesday, Friday" or
/// "Weekdays"), parsed back with Enum.Parse&lt;DayOfWeekFlags&gt; (which natively supports
/// comma-separated flag names) rather than sent as a raw int, matching the string-not-raw-enum
/// convention NodeConfigZoneDto.Kind already uses.</summary>
public record NodeConfigScheduleWindowDto(Guid Id, string Days, TimeOnly StartTime, TimeOnly EndTime);

/// <summary>RecordingMode is one of "Continuous"/"Motion"/"Schedule"/"Event" (RecordingMode enum's
/// ToString() — see ISettingsResolver's Recording.Mode key). Sent as a string here, same convention
/// as NodeConfigZoneDto.Kind: NodeWorker is the one place that actually branches on it, and parses
/// it back into the enum there (Enum.TryParse, falling back to Continuous for anything
/// unrecognized) rather than the DTO carrying the enum type directly. MotionPreRollSeconds/
/// MotionPostRollSeconds (M8 pass 3) are how far before a motion event started, and after it ended,
/// a segment still counts as worth keeping for a Motion- or Event-mode camera — irrelevant, and
/// unused by the node, for Continuous or Schedule. Kept as two separate values (not one shared
/// "padding") because pre-roll and post-roll are answering genuinely different questions and an
/// operator may reasonably want them different — a long pre-roll costs nothing extra (recording is
/// already continuous either way) but a long post-roll means keeping more low-signal footage per
/// event.
///
/// EventsServiceUri (M8 pass 6): the node's first reason to make its own ONVIF SOAP calls rather
/// than only receiving ready-made RTSP URIs — ONVIF PullPoint event polling is a continuous,
/// long-lived conversation the web tier can't pre-resolve into a one-shot value the way
/// GetStreamUri's lookup already is. Still pre-*resolved*, though: this is the Events service's own
/// XAddr (from the capability prober's raw category map, CameraCapabilities.RawProbeJson —
/// deliberately NOT the same as Camera.DeviceServiceUri, ONVIF's device-management entry point),
/// handed over directly so the node never needs its own GetCapabilities round trip just to find out
/// where to send CreatePullPointSubscription. Null for a camera with no advertised Events service.
///
/// ScheduleWindows: only meaningful for Recording.Mode=Schedule — evaluated against the node's own
/// local system clock, not a stored timezone (see NodeWorker.IsWithinSchedule's doc comment for
/// why).</summary>
/// <summary>IntegrationKey/IntegrationBaseUri drive the vendor-plugin session a node starts
/// alongside its ONVIF one (see ICameraIntegrationProvider). Both null for the majority of cameras,
/// which need nothing beyond ONVIF. BaseUri is the camera's own scheme+host+port — the plugin
/// appends whatever path its vendor API uses — sent explicitly rather than parsed out of
/// EventsServiceUri on the node, since a camera can need an integration while having no ONVIF events
/// service at all.</summary>
/// <summary>SegmentSeconds: how long each recorded Main-stream file covers before ffmpeg rolls to the
/// next one (RecordingSessionOptions.SegmentSeconds, default 60 — unset until this existed).
/// Confirmed live as the dominant cost of a late seek within a segment: Playback fetches a segment's
/// bytes sequentially and only starts at the target instant once its bytes are actually buffered, so
/// a 60s segment at this fleet's real sizes (24-44MB) means downloading up to that whole amount before
/// landing on a scrub target near its end. Shorter segments cap that worst case proportionally, at the
/// cost of more Segments rows and more small files on disk. Same "restart to apply" story as a
/// changed Privacy mask signature — it's baked into ffmpeg's own `-f segment` invocation at start, not
/// something a later reconcile can adjust on an already-running process.</summary>
public record NodeConfigCameraDto(Guid CameraId, string Name, string? Username, string? Password,
    List<NodeConfigStreamDto> Streams, int? RetentionDays, long? QuotaBytes, List<NodeConfigZoneDto> Zones,
    string RecordingMode, int MotionPreRollSeconds, int MotionPostRollSeconds,
    string? EventsServiceUri, List<NodeConfigEventTagRuleDto> EventTagRules,
    List<NodeConfigScheduleWindowDto> ScheduleWindows,
    string? IntegrationKey = null, string? IntegrationBaseUri = null, int SegmentSeconds = 60,
    /// <summary>Object detection plan: whether this camera's node should watch it for AI object
    /// detection at all — see Camera.AiDetectionEnabled's own doc comment.</summary>
    bool AiDetectionEnabled = false,
    /// <summary>Object detection plan decision 9: Camera.MotionDetectionSource's enum name (e.g.
    /// "ServerMotion"), or null if not yet configured — same string-wire-format-parsed-node-side
    /// pattern RecordingMode already uses. Only meaningful when RecordingMode is "Motion".</summary>
    string? MotionDetectionSource = null,
    /// <summary>AI detection's own confidence/IoU thresholds — per-camera (Camera &rarr; Node &rarr;
    /// Global, same chain RetentionDays/RecordingMode already resolve through), not global-only:
    /// a camera prone to a specific misdetection (see the driveway truck/car "best frame" bug this
    /// was added alongside) may need its own threshold tuned independently of the deployment-wide
    /// default. Defaults match aitest's own proven values.</summary>
    double AiConfidence = 0.35, double AiIou = 0.5,
    /// <summary>Which of this camera's streams AI detection actually watches — "Main" or "Sub",
    /// same Camera &rarr; Node &rarr; Global override chain, global default "Sub" (preserves the
    /// behavior from before this was configurable at all: NodeWorker.ReconcileVision used to
    /// hardcode the "Sub" stream unconditionally).</summary>
    string AiDetectionStreamRole = "Sub",
    /// <summary>How to orient this camera's reported watch-stream dimensions before the detection
    /// profile is built from them — "Auto" (trust what the camera reports), "Landscape" or "Portrait".
    /// Same Camera &rarr; Node &rarr; Global chain and string-wire-format-parsed-node-side shape as
    /// AiDetectionStreamRole above; see LarisVMS.Node.DetectionOrientation for why a corridor-mounted
    /// camera needs this and why it is an operator setting rather than a measurement. Defaults "Auto"
    /// so an older, not-yet-updated node's deserialization keeps today's exact behavior.</summary>
    string AiDetectionOrientation = "Auto",
    /// <summary>Detection/hardware-acceleration overhaul, pass 0 — see Camera.ServerMotionEnabled's
    /// own doc comment. Plain per-camera bool, not resolved through the Camera &rarr; Node &rarr;
    /// Global settings chain, same shape as AiDetectionEnabled above.</summary>
    bool ServerMotionEnabled = true,
    /// <summary>Detection/hardware-acceleration overhaul pass 3c-2: Camera.MotionRegionMode's enum
    /// name ("Polygon" or "Grid") — same string-wire-format-parsed-node-side pattern
    /// MotionDetectionSource above already uses. Defaults "Polygon" (today's exact behavior) so an
    /// older, not-yet-updated node's deserialization never silently reads as Grid.</summary>
    string MotionRegionMode = "Polygon",
    /// <summary>Camera.MotionGridSize — only meaningful when MotionRegionMode is "Grid".</summary>
    int MotionGridSize = 32,
    /// <summary>Camera.MotionGridMask — only meaningful when MotionRegionMode is "Grid". Null/empty
    /// means nothing is masked (watch the whole frame), same as an absent value always has.</summary>
    string? MotionGridMask = null,
    /// <summary>Camera.MotionGridSensitivity — the Grid-mode equivalent of a ServerMotion zone's own
    /// Sensitivity, needed because Grid mode's single aggregate region (see NodeWorker.ReconcileMotion)
    /// still has to compare its own score against *something* to decide when a span opens. Same
    /// 0.03 default Zone.Sensitivity itself uses.</summary>
    double MotionGridSensitivity = 0.03);
/// <summary>A camera this node has leftover Segments for but is no longer assigned to record
/// (reassigned to a different node, or deleted) — StorageManager's orphaned-folder sweep uses
/// RetentionDays here so leftover footage still ages out on the same schedule it always would have,
/// instead of a generic fallback the camera's own settings never actually specified. Resolved the
/// same global -&gt; per-node -&gt; per-camera way NodeConfigCameraDto.RetentionDays is (scoped to
/// *this* node, since that's whose copy is being aged out) — null only when nothing resolves it at
/// all, which StorageManager falls back to a flat default for.</summary>
public record NodeConfigOrphanedCameraDto(Guid CameraId, int? RetentionDays);

/// <summary>AdaptiveStreamingEnabled (M18) is the server-side "LiveView.AdaptiveStreamingEnabled"
/// setting, resolved once here rather than left for the node to fetch on its own — same pattern as
/// every other node-wide setting already flowing through this DTO (WatermarkPercent). The node is
/// the sole, authoritative gate: it drives SubLiveSession reconciliation directly (see
/// NodeWorker.ReconcileLiveSub) — when false, Sub live sessions are torn down/never started, so a
/// disabled toggle actually stops the extra RTSP pulls, not just hides the option client-side.
/// LarisVMS.Web's /live proxy forwards a viewer's `?role=sub` request as-is and never itself checks
/// this flag — there's nothing to gate there: if the node has no Sub session running (toggle off, or
/// hasn't reconciled since it flipped), the node's own /live route already falls back to Main, and a
/// second independent check on the Web tier would just be two places that can disagree about the
/// same underlying fact. Defaults true purely so an older, not-yet-updated node (whose own
/// NodeConfigResponse deserialization would otherwise leave a bool defaulted to false) doesn't
/// silently read "disabled" — this default is never actually seen by a current build, which always
/// gets a real resolved value from GetConfigAsync.</summary>
public record NodeConfigResponse(List<NodeConfigCameraDto> Cameras, string? StorageRootPath, int WatermarkPercent,
    string MediaSigningKey, List<NodeConfigOrphanedCameraDto> OrphanedCameras, bool AdaptiveStreamingEnabled = true,
    /// <summary>Object detection plan decision 2: the resolved Node.AiAccelerator enum name (e.g.
    /// "Auto", "Nvidia") — the node combines this with its own local AccelCapabilityProber probe
    /// via AccelSelection.ChooseAccelerator to decide which LarisVMS.Vision.Service.&lt;Accel&gt;
    /// variant (if any) to run. Defaults to "Auto" for the same reason AdaptiveStreamingEnabled
    /// defaults true — an older, not-yet-updated node's deserialization shouldn't silently read as
    /// something more restrictive than the server actually resolved; a current build always gets a
    /// real value from GetConfigAsync.</summary>
    string AiAccelerator = "Auto",
    /// <summary>Object detection plan decision 7's ReportIdleDetections — global, not per-camera,
    /// since "am I interested in idle objects at all, ever" reads as a deployment-wide testing
    /// toggle rather than something that varies camera by camera. Threaded through to every
    /// VisionStartCameraRequest NodeWorker builds.</summary>
    bool ReportIdleDetections = false,
    /// <summary>Detection.AspectMode's enum name (e.g. "Letterbox") — node-scoped like
    /// DetectionModelFamily below: one choice governs how every camera on a node fits its own aspect
    /// ratio into D-FINE's square input. Retires the old AiDetectionWidth/AiDetectionHeight (a single
    /// global decode resolution shared by every camera regardless of shape, detection/hardware-
    /// acceleration overhaul pass 1) — decode resolution is now derived per camera from its own real
    /// Sub-stream dimensions (NodeConfigStreamDto.Width/Height) instead.</summary>
    string AspectMode = "Letterbox",
    /// <summary>Detection.ModelFamily's enum name (e.g. "Auto", "DFine") — node-scoped like
    /// AiAccelerator (Setting + SettingOverride(Scope.Node)), not per-camera: one Vision Service
    /// process serves every camera on a node from the same loaded model, so which model family it
    /// uses is inherently a per-node choice, not a per-camera one the way Confidence/Iou are.
    /// NodeWorker resolves "Auto" against this node's own accelerator via
    /// DetectionModelSelection.Choose before it ever reaches VisionStartCameraRequest.</summary>
    string DetectionModelFamily = "Auto",
    /// <summary>Detection.DFineWeights' enum name (e.g. "Obj2Coco") — same node-scoped resolution
    /// as DetectionModelFamily, only meaningful when that resolves to "DFine".</summary>
    string DFineWeights = "Obj2Coco",
    /// <summary>How long a label's hysteresis waits after motion stops before actually closing the
    /// span (MotionHysteresis's endAfter) — global, same reasoning as the other Detection.* fields
    /// above. Was hardcoded to zero, which closed a span on the very first quiet frame; a single
    /// missed/occluded detection, or a track briefly classified Idle before resuming Moving, then
    /// reopened as a brand-new span/snapshot instead of continuing the same one. A real grace period
    /// absorbs that flicker while still finalizing the snapshot once the object is genuinely gone or
    /// has settled into Idle for good.</summary>
    int AiIdleTimeoutSeconds = 10,
    /// <summary>Detection/hardware-acceleration overhaul, pass 3b: Detection.EnableHighResReDetection,
    /// resolved Global -> Node like AspectMode/DetectionModelFamily above — one Vision Service process
    /// per node, so whether it runs motion-guided native-scale Main-stream re-detection at all is a
    /// per-node choice. Defaults false (opt-in) for the same "don't silently add CPU/GPU load an
    /// older, not-yet-updated node build's deserialization would answer" reasoning ReportIdleDetections
    /// already documents above.</summary>
    bool EnableHighResReDetection = false,
    /// <summary>Detection.EnableVisionDebugImages — diagnostic-only, resolved Global -&gt; Node like
    /// EnableHighResReDetection above. When true, Vision Service writes one cropped JPEG per
    /// re-detection trigger to logs\vision-debug\ for diagnosing snapshot box alignment. Defaults
    /// false (opt-in) — an install that wants it keeps a global Setting row = 'true'.</summary>
    bool EnableVisionDebugImages = false,
    /// <summary>Detection.GpuPreprocessing (pass 4a) — resolved Global -&gt; Node. When true the
    /// Vision Service moves per-frame colour conversion + normalize off the CPU onto the accelerator
    /// (an ONNX preprocessing head + nv12 ffmpeg output). Vendor-neutral, opt-in, defaults false.</summary>
    bool GpuPreprocessing = false,
    /// <summary>Logging.Level — the deployment-wide minimum log level, applied to this node's own
    /// file logger and pushed on to its Vision Service. One of Trace/Debug/Information/Warning/Error;
    /// anything unparseable falls back to Information. Framework request-pipeline noise stays floored
    /// at Warning regardless.</summary>
    string LogLevel = "Information",
    /// <summary>Detection.YoloXSize's enum name (Nano/Tiny/S/M/L/X) — node-scoped like
    /// DetectionModelFamily/DFineWeights, only meaningful when the family resolves to "YoloX". Picks
    /// which YOLOX ONNX the node fetches from the server (YOLOX models aren't bundled) and its
    /// network input size. Appended last so the positional constructor calls (NodeService) stay
    /// stable; defaults "S".</summary>
    string YoloXSize = "S",
    /// <summary>Detection.MaxFps — the ceiling on how many frames per second per camera reach the
    /// detection model. The Vision Service's ffmpeg still decodes the Sub stream in real time, but an
    /// `fps=` filter drops the rest before inference, so the GPU idles between frames instead of
    /// running flat out. Node-scoped. 0 = no cap (decode-rate). Default 10 — plenty for NVR object
    /// tracking, and where the live overlay poll already tops out.</summary>
    int MaxDetectionFps = 10,
    /// <summary>Detection.HiResSnapshots (Pass F) — resolved Global -&gt; Node like the other
    /// Detection.* flags. When true the Vision Service decodes each Sub stream at up to its native
    /// resolution (long edge capped to SnapshotImageCapture.MaxDimension) and crops the eager
    /// AI-detection snapshot from that larger frame instead of the detector's network buffer —
    /// sharper only where the Sub stream itself is bigger than the network size. Opt-in, defaults
    /// false; forces GpuPreprocessing off per camera while on. Appended last so NodeService's
    /// positional construction stays stable.</summary>
    bool HiResSnapshots = false);

/// <summary>One completed MotionSpan, batch-reported the same way SegmentReportItem is — see
/// NodeService.RecordMotionSpansAsync for why plain REST + EF insert is enough here despite the
/// plan flagging SqlBulkCopy for this table: a span is only written once it *closes* (debounced by
/// MotionHysteresis), not per frame, so real write volume looks like Segments' (a handful of rows
/// per camera per interesting event), not per-frame Detections-scale volume.
///
/// ZoneId is null for a camera-pushed (M8 pass 6) span — an ONVIF PullPoint event has no concept of
/// one of our own drawn ServerMotion zones — and non-null for a ServerMotion span. EventTagRuleId
/// (M8 pass 8) is set only for a custom-tag-sourced span; NodeService infers Source from whichever
/// of the two is set (both null = built-in CameraEvent motion) instead of carrying a separate field.</summary>
///
/// DetectionKind is set only for a span produced by an object-detection topic (person/vehicle/face —
/// see CameraEventClassifier.ClassifyDetection); it joins the upsert identity alongside ZoneId and
/// EventTagRuleId so two object classes detected at the same instant stay separate spans rather than
/// colliding on the same row.</summary>
/// DetectedObjectCategory/DetectedObjectLabel/BestFrame* (object detection plan decisions 5 and
/// 10) are set only for an AiDetection-sourced span. DetectedObjectCategory is the resolved
/// category *name* (e.g. "Vehicle") — the node never assigns an Id or color; NodeService.
/// RecordMotionSpansAsync find-or-creates the DetectedObjectCategory row and auto-picks a color
/// only the first time a name is ever seen. BestFrameAtUtc/BestBoxX/Y/W/H/BestBoxConfidence are
/// whatever LarisVMS.Vision.Service's MovementClassifier currently has as the best-scoring frame
/// for this track — on a checkpointed in-progress span this can only improve on a later report
/// (the Vision-side tracking is monotonic), so the upsert here just overwrites.</summary>
public record MotionSpanReportItem(Guid CameraId, Guid? ZoneId, DateTime StartUtc, DateTime EndUtc, double Score,
    Guid? EventTagRuleId = null, DetectionKind? DetectionKind = null,
    string? DetectedObjectCategory = null, string? DetectedObjectLabel = null,
    DateTime? BestFrameAtUtc = null, double? BestBoxX = null, double? BestBoxY = null,
    double? BestBoxW = null, double? BestBoxH = null, double? BestBoxConfidence = null);

/// <summary>M8 pass 6: one raw ONVIF PullPoint notification, reported the same batched way a
/// MotionSpan or Segment is. IsMotion (see CameraEventClassifier, run on the node as each
/// notification arrives) tells the web tier whether this event also produced a MotionSpan — it
/// doesn't re-derive that from OnvifTopic/PayloadJson itself.</summary>
public record CameraEventReportItem(Guid CameraId, string OnvifTopic, DateTime ReceivedUtc, string? PayloadJson, bool IsMotion);

public record SegmentReportItem(Guid CameraId, string StreamRole, DateTime StartUtc, DateTime EndUtc,
    string FilePath, long SizeBytes, string? Codec, int? Width, int? Height, bool HasAudio);

public record NodeStatusReportItem(Guid CameraId, string State, DateTime? LastSegmentAt, string? Error);

/// <summary>Reported by the node's StorageManager after it deletes files on disk (retention,
/// per-camera quota, or watermark eviction) — the web deletes the matching Segment rows so the DB
/// index never claims a file that no longer exists.</summary>
public record SegmentDeleteRequest(List<string> FilePaths);

/// <summary>Real resolution/codec parsed from ffmpeg's own stderr when it opens a camera's stream,
/// plus (M11) periodic health signals — real-time fps/bitrate from ffmpeg's progress line and a
/// cumulative reconnect count, both from the same RecordingSession. Only ever reported for the Main
/// stream today, since that's the only one the node actually opens with ffmpeg (Sub/Third aren't
/// consumed by anything until M5's live view).
///
/// Width/Height/Codec are sent once per ffmpeg (re)start (more trustworthy than ONVIF's advertised
/// VideoEncoderConfiguration, which some cameras omit entirely) and null on every other report;
/// AudioCodec/AudioSampleRateHz likewise, from the audio line of the same stream summary (and never
/// sent at all for a camera with no audio track); Fps/BitrateKbps/ReconnectCount are sent on
/// NodeWorker's periodic health tick and refreshed there regardless of whether the connection just
/// changed. Deliberately one merged DTO rather than several — they all flow through the exact same
/// pending-queue/flush/UpdateStreamInfoAsync pipeline, and NodeService.UpdateStreamInfoAsync
/// preserves whichever fields a given report didn't include (see its own doc comment) rather than
/// one report type clobbering another's data. New optional fields also mean a node still running an
/// older binary keeps reporting successfully — its JSON simply omits them, which binds as null and
/// preserves whatever is already stored.</summary>
public record StreamInfoReportItem(Guid CameraId, string StreamRole, int? Width, int? Height, string? Codec,
    int? Fps = null, int? BitrateKbps = null, int? ReconnectCount = null,
    string? AudioCodec = null, int? AudioSampleRateHz = null);
