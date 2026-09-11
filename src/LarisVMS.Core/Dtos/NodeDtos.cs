using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Dtos;

// Wire DTOs for the node control plane (POST /api/nodes/*). Shared via Core so LarisVMS.Node can
// serialize/deserialize them without referencing LarisVMS.Infrastructure or LarisVMS.Web.

public record NodeRegisterRequest(string RegistrationKey, string Hostname, string? Version, string? Platform,
    /// <summary>The storage path (and optional archive path) install-node.ps1 was given
    /// (-StorageRoot / -ArchiveRoot). Persisted on the new Node row so storage config is per-node
    /// with no global default. Null from an older install-node.ps1 — the node then has no storage
    /// path and records nothing until an admin sets one on Admin/Nodes.</summary>
    string? StorageRootPath = null, string? ArchiveRootPath = null);
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
    DateTime? SentAtUtc = null, List<string>? DetectedEncoders = null, List<string>? DetectedAccelerators = null,
    /// <summary>Archive-storage: free/total bytes on the archive volume, and whether the primary
    /// volume is currently over the storage watermark (footage being archived/deleted early). All
    /// null/false on an older node build or one with no archive root configured.</summary>
    long? ArchiveFreeBytes = null, long? ArchiveTotalBytes = null, bool StoragePressureActive = false,
    /// <summary>Replay hardening (failover plan phase 5a): the single-use nonce this node received in
    /// the previous heartbeat response (NodeHeartbeatResponse.NextNonce), echoed back verbatim. Null
    /// from a node that has never been issued one — an older build, or the first heartbeat after
    /// (re-)registration. The server treats absent-vs-present-but-wrong as genuinely different cases:
    /// absent against a stored nonce = a not-yet-upgraded node (allowed, stored value cleared);
    /// present but not matching = a replay (401). Never normalized to "" — see the failover plan's
    /// nonce-rollout note.</summary>
    string? Nonce = null,
    /// <summary>Failover plan phase 1: what the node's client HTTPS endpoint is actually doing right
    /// now — the port it bound (null if it isn't running one), the NotAfter and self-signed-ness of
    /// the certificate it's serving, and the last error it hit standing the endpoint up or reloading
    /// the cert (null when healthy). All informational; the ticket endpoint uses them to decide
    /// whether a browser can be routed straight to this node.</summary>
    int? ClientEndpointReportedPort = null, DateTime? ClientEndpointCertNotAfterUtc = null,
    bool? ClientEndpointCertIsSelfSigned = null, string? ClientEndpointLastError = null,
    /// <summary>Failover plan phase 3: this node's own reads of the partner nodes it was told to
    /// probe (<see cref="NodeConfigResponse.PartnersToProbe"/>), one entry per partner it actually
    /// managed to reach a verdict on this cycle. The server persists these as this node's outgoing
    /// votes; <c>RecordingFailoverService</c> tallies them alongside central's own live probe and any
    /// assigned proxy's. Null/empty from an older node build or one with nothing to probe.</summary>
    List<NodePartnerHealthReport>? PartnerHealthReports = null);

/// <summary>Failover plan phase 3: one recorder node a voter (a partner node, or a media proxy) is
/// asked to probe for the recording-failover quorum. <see cref="Host"/>/<see cref="Port"/> are the
/// node's plain-HTTP LAN address (<c>LastIpAddress</c> + <c>LivePort</c>) — the voter GETs
/// <c>http://{Host}:{Port}/health</c> with a short timeout.</summary>
public record NodePartnerProbeDto(Guid NodeId, string Host, int Port);

/// <summary>Failover plan phase 3: one voter's verdict on one subject node's <c>/health</c> this
/// cycle. <see cref="ServiceRunning"/> is true only on a clean HTTP 200 whose body parsed as
/// <see cref="NodeHealthDto"/>; a non-200, a timeout, or an unparseable body is false (and never a
/// bare TCP accept). <see cref="Detail"/> is a short human-readable reason when false.</summary>
public record NodePartnerHealthReport(Guid NodeId, bool ServiceRunning, DateTime CheckedAtUtc, string? Detail = null);

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

/// <summary>NextNonce (failover plan phase 5a): the single-use value the node must echo on its next
/// heartbeat (NodeHeartbeatRequest.Nonce). Issued fresh every heartbeat that passes validation. Null
/// only on a 401 replay rejection (the node never sees a 200 body then anyway).
///
/// NewSecret (phase 5b): a freshly minted bearer secret the node must switch to immediately and
/// persist to its node.config. Non-null only when automatic rotation is due (Node.SecretRotationDays,
/// 0 = never) or an admin forced one, and only on a heartbeat that already passed nonce validation —
/// a replayed/forged check-in can never harvest one. The server keeps the old secret working
/// (PreviousApiKeyHash) until the node first authenticates with the new one, so there is no downtime
/// window.</summary>
public record NodeHeartbeatResponse(int IntervalSeconds, NodeUpdateInfoDto? UpdateAvailable = null,
    string? NextNonce = null, string? NewSecret = null);

/// <summary>Metadata for a large Vision Service native dependency the node package doesn't bundle and
/// fetches from the server on demand — currently only <c>onnxruntime_providers_cuda.dll</c> (~320 MB),
/// downloaded by a node that resolved the CUDA backend so it isn't shipped to every node. The server
/// serves the file from a cache directory seeded by <c>deploy.ps1</c>; <see cref="Sha256"/> is
/// verified by the node before the file is put in place. Null from the endpoint means the server has
/// no copy seeded.</summary>
public record VisionNativeInfo(string Name, string Sha256, long SizeBytes);

/// <summary>Failover plan phase 3: the body of the recorder node's unauthenticated
/// <c>GET /health</c> on its plain-HTTP LAN Kestrel port. A quorum voter (the partner node, central,
/// or an assigned proxy) treats "HTTP 200 + a body that parses to this" as <em>service running</em>;
/// a non-200, a timeout, or an unparseable body is <em>service not running</em> — a bare TCP accept
/// or an ICMP echo must never count, since a lingering socket or a proxy answering for a dead process
/// has to read as down. <see cref="RecordingCameraCount"/> is informational (a node that is up but
/// recording nothing is still up); <see cref="UptimeSeconds"/> lets a probe spot a crash-loop.</summary>
public record NodeHealthDto(string? Version, int RecordingCameraCount, long UptimeSeconds);

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
    double MotionGridSensitivity = 0.03,
    /// <summary>Archive-storage: whether footage for this camera on this node is moved to the archive
    /// volume when primary retention would delete it (Camera &rarr; Node &rarr; Global, key
    /// "Archive.Enabled"), and how long it is then kept on the archive volume before the node's
    /// archive-expiry sweep deletes it ("Archive.RetentionDays"; 0/null = keep forever). Defaults
    /// off/null so an older node's deserialization never archives.</summary>
    bool ArchiveEnabled = false, int? ArchiveRetentionDays = null,
    /// <summary>Detection.RejectMotionJitter / Detection.MotionJitterPixels — per-camera
    /// (Camera &rarr; Node &rarr; Global, same chain AiConfidence resolves through), because
    /// box-wobble jitter is a property of one camera's view (a driveway with a parked vehicle in
    /// frame), not a deployment-wide fact. RejectMotionJitter off by default restores the proven
    /// pre-0.188 movement classifier; MotionJitterPixels (1-15, default 3) is the absolute
    /// centroid-travel floor the rejection path requires, only acted on when the toggle is on.
    /// Appended last so the positional NodeService construction stays stable.</summary>
    bool RejectMotionJitter = false, int MotionJitterPixels = 3);
/// <summary>A camera this node has leftover Segments for but is no longer assigned to record
/// (reassigned to a different node, or deleted) — StorageManager's orphaned-folder sweep uses
/// RetentionDays here so leftover footage still ages out on the same schedule it always would have,
/// instead of a generic fallback the camera's own settings never actually specified. Resolved the
/// same global -&gt; per-node -&gt; per-camera way NodeConfigCameraDto.RetentionDays is (scoped to
/// *this* node, since that's whose copy is being aged out) — null only when nothing resolves it at
/// all, which StorageManager falls back to a flat default for.</summary>
public record NodeConfigOrphanedCameraDto(Guid CameraId, int? RetentionDays,
    /// <summary>Archive-storage: same as NodeConfigCameraDto's fields, resolved for this no-longer-
    /// assigned camera so the archive-expiry sweep still ages its leftover archived footage out on
    /// the schedule its own settings specified. Defaults off/null.</summary>
    bool ArchiveEnabled = false, int? ArchiveRetentionDays = null);

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
    /// <summary>Detection.DFineTensorRtMode — "Off" (default) / "FP32" / "FP16", node-scoped like the
    /// other Detection.* fields (Setting + SettingOverride(Scope.Node)). Only acted on when the family
    /// resolves to "DFine" and the node has the machine-local Vision:EnableTensorRt set with a working
    /// TensorRT SDK. Supersedes the machine-local Vision:DFineTensorRtMode (still honoured as a
    /// fallback when this arrives blank from an older server). FP32 runs the plain model through
    /// TensorRT; FP16 would need a mixed-precision model that isn't producible yet, so a node set to
    /// FP16 transparently runs FP32 until one is bundled. Appended last so the positional NodeService
    /// construction stays stable; defaults "Off" so an older node reads the safe value.</summary>
    string DFineTensorRtMode = "Off",
    /// <summary>Archive-storage: the resolved archive volume root for this node (Node.ArchiveRootPath
    /// or the global Archive.RootPath setting) — the secondary (SMB / USB) location aged-out footage
    /// is moved to when a camera's ArchiveEnabled is set. Null when no archive root is configured, in
    /// which case archiving is inert regardless of the per-camera flag. Must be a path outside
    /// StorageRootPath. Appended last so the positional NodeService construction stays stable.</summary>
    string? ArchiveRootPath = null,
    /// <summary>Detection.SnapshotMotionAccuracy (global) — when true (the default), the Vision
    /// Service rejects raw-detection-box jitter so a parked vehicle stops flickering to Moving, and
    /// finalizes a label's span promptly once its object leaves rather than holding it open long
    /// enough for a later unrelated object of the same type to merge into it. False restores the
    /// prior behaviour for A/B comparison. Threaded into every VisionStartCameraRequest and part of
    /// NodeWorker's restart signature. Appended last so the positional NodeService construction
    /// stays stable; defaults true so an older node build reads the improved behaviour.</summary>
    bool SnapshotMotionAccuracy = true,
    /// <summary>Failover plan phase 1: the node's client HTTPS endpoint (a browser connecting straight
    /// to the node for live/playback, skipping the central relay). ClientEndpointEnabled + Port
    /// establish the second Kestrel listener — a change to either needs a node restart, the same
    /// "restart to apply" model SegmentSeconds uses. ClientCertPfxPath/Password name the certificate
    /// to serve (password sent decrypted over this HTTPS control channel and cached in the
    /// DPAPI-protected node.config, identical handling to NodeConfigCameraDto.Password).
    /// ClientEndpointAllowInsecure is what permits the self-signed fallback — with it false the node
    /// never generates one and the listener simply doesn't start when no valid pfx resolves. A local
    /// %ProgramData%\LarisVMS\client-endpoint.json on the node host overrides all of these. WebOrigin
    /// is this server's own scheme://host, echoed so the node can send it as
    /// Access-Control-Allow-Origin on its client-facing routes. All defaulted so an older node
    /// deserializes cleanly and simply never stands an endpoint up.</summary>
    bool ClientEndpointEnabled = false, int ClientEndpointPort = 0, string? ClientCertPfxPath = null,
    string? ClientCertPfxPassword = null, bool ClientEndpointAllowInsecure = false, string? WebOrigin = null,
    /// <summary>Failover plan phase 3: recorder nodes this node is the backup for and must probe for
    /// the recording-failover quorum — it GETs each one's <c>http://{Host}:{Port}/health</c> every
    /// ~15s and carries the verdicts back in <see cref="NodeHeartbeatRequest.PartnerHealthReports"/>.
    /// Null/empty for a node that backs up nobody, or an older build. Appended last so the positional
    /// NodeService construction stays stable.</summary>
    List<NodePartnerProbeDto>? PartnersToProbe = null,
    /// <summary>Detection.DepartureGraceSeconds (global, 1-10) — how long a label's span with no
    /// instance classified Moving is held before SnapshotMotionAccuracy's early-finalize flushes it
    /// as "the object left frame". Was a hard-coded 5s in CameraDetectionPipeline. Threaded into
    /// every VisionStartCameraRequest and part of NodeWorker's restart signature. Appended last so
    /// the positional NodeService construction stays stable; defaults 5 = the previous constant.</summary>
    int DepartureGraceSeconds = 5,
    /// <summary>Detection.Backend (node-scoped, "BuiltIn"/"ExternalHttp") plus the external
    /// service's address, model, and model input size — resolved Node &rarr; Global like
    /// DetectionModelFamily. "ExternalHttp" makes the Vision Service run no local model: it decodes +
    /// tracks + reports exactly as before, but each frame is POSTed to
    /// <see cref="ExternalInferenceUrl"/> and the JSON boxes come back through HttpDetectionEngine.
    /// This is the one detection path that needs no local accelerator, so NodeWorker starts the
    /// Vision Service for it even on a GPU-less node. All appended last so the positional NodeService
    /// construction stays stable; defaults keep an older node on the built-in engine.</summary>
    string DetectionBackend = "BuiltIn",
    string ExternalInferenceUrl = "",
    string ExternalInferenceModel = "",
    int ExternalInferenceInputSize = 640,
    /// <summary>Bearer token for the external service (Authorization header on every /v1/detect
    /// call) — resolved Node &rarr; Global like the fields above. Blank when the service needs no
    /// auth or the built-in backend is in use. Appended last so the positional NodeService
    /// construction stays stable.</summary>
    string ExternalInferenceApiKey = "");

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
    double? BestBoxW = null, double? BestBoxH = null, double? BestBoxConfidence = null,
    /// <summary>Peak distinct moving instances of this label over the span (object detection: the
    /// "x2" / "x3" snapshot badge count). Null for a non-AiDetection span and for an older node that
    /// doesn't report it; NodeService keeps the running max across checkpoint/coalesce updates the
    /// same way it does Score.</summary>
    int? MovingCount = null);

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

/// <summary>Reported by the node's StorageManager after it MOVES a segment file from the primary
/// volume to the archive volume (primary retention would have deleted it, and archiving is enabled).
/// The web updates the Segment row in place — FilePath to the archive path, StorageTier to Archive,
/// ArchivedAt, and SizeBytes (unchanged for a plain move; changed once phase 3 re-encodes). One
/// statement per item, idempotent: a report for a row already at NewFilePath is a no-op success.</summary>
public record SegmentRelocateItem(string OldFilePath, string NewFilePath, long SizeBytes);
public record SegmentRelocateRequest(List<SegmentRelocateItem> Items);

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
