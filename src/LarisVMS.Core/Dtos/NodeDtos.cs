using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Dtos;

// Wire DTOs for the node control plane (POST /api/nodes/*). Shared via Core so LarisVMS.Node can
// serialize/deserialize them without referencing LarisVMS.Infrastructure or LarisVMS.Web.

public record NodeRegisterRequest(string RegistrationKey, string Hostname, string? Version, string? Platform);
public record NodeRegisterResponse(Guid NodeId, string Secret, string MediaSigningKey);

/// <summary>SentAtUtc is the node's own DateTime.UtcNow at the moment it builds this request — the
/// web server compares that against its own receive-time to measure this node's OS clock skew (see
/// Node.ClockSkewSeconds). Defaulted rather than required so an older node build (pre-dating this
/// field) still deserializes cleanly against a newer Web — it just never gets a skew measurement.</summary>
public record NodeHeartbeatRequest(string? Version, long? FreeBytes = null, long? TotalBytes = null, int? LivePort = null, DateTime? SentAtUtc = null);

/// <summary>Recorder-node auto-update: a genuinely newer NodeBuildVersion exists for this node's
/// reported Platform (NodeVersionComparer.IsNewer), and NodeAutoUpdate.Enabled is on. DownloadUrl is
/// an absolute URL back to this same server's /api/nodes/download/{buildId} — reusing the node's own
/// Bearer nodeId:secret credentials, same auth as every other /api/nodes/* route (NodeAuthMiddleware).
/// Sha256 is what LarisVMS.Node.Update.UpdateService verifies the download against before applying
/// it; a mismatch aborts without touching the running binary.</summary>
public record NodeUpdateInfoDto(string Version, string DownloadUrl, string Sha256, long SizeBytes);

public record NodeHeartbeatResponse(int IntervalSeconds, NodeUpdateInfoDto? UpdateAvailable = null);

public record NodeConfigStreamDto(Guid StreamId, string Role, string RtspUri,
    string? Codec, int? Width, int? Height, bool HasAudio);

/// <summary>M8: only ServerMotion/Ignore zones are ever sent here — CameraMotion/Privacy don't
/// drive anything on the node (CameraMotion push happens from LarisVMS.Web over ONVIF in a
/// follow-up pass; Privacy has no node-side effect yet at all), so there's no reason to hand a
/// recorder node polygon data it can't act on.</summary>
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
public record NodeConfigCameraDto(Guid CameraId, string Name, string? Username, string? Password,
    List<NodeConfigStreamDto> Streams, int? RetentionDays, long? QuotaBytes, List<NodeConfigZoneDto> Zones,
    string RecordingMode, int MotionPreRollSeconds, int MotionPostRollSeconds,
    string? EventsServiceUri, List<NodeConfigEventTagRuleDto> EventTagRules,
    List<NodeConfigScheduleWindowDto> ScheduleWindows,
    string? IntegrationKey = null, string? IntegrationBaseUri = null);
/// <summary>A camera this node has leftover Segments for but is no longer assigned to record
/// (reassigned to a different node, or deleted) — StorageManager's orphaned-folder sweep uses
/// RetentionDays here so leftover footage still ages out on the same schedule it always would have,
/// instead of a generic fallback the camera's own settings never actually specified. Resolved the
/// same global -&gt; per-node -&gt; per-camera way NodeConfigCameraDto.RetentionDays is (scoped to
/// *this* node, since that's whose copy is being aged out) — null only when nothing resolves it at
/// all, which StorageManager falls back to a flat default for.</summary>
public record NodeConfigOrphanedCameraDto(Guid CameraId, int? RetentionDays);

public record NodeConfigResponse(List<NodeConfigCameraDto> Cameras, string? StorageRootPath, int WatermarkPercent,
    string MediaSigningKey, List<NodeConfigOrphanedCameraDto> OrphanedCameras);

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
public record MotionSpanReportItem(Guid CameraId, Guid? ZoneId, DateTime StartUtc, DateTime EndUtc, double Score,
    Guid? EventTagRuleId = null, DetectionKind? DetectionKind = null);

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
/// Fps/BitrateKbps/ReconnectCount are sent on NodeWorker's periodic health tick and refreshed there
/// regardless of whether the connection just changed. Deliberately one merged DTO rather than two —
/// both flow through the exact same pending-queue/flush/UpdateStreamInfoAsync pipeline, and
/// NodeService.UpdateStreamInfoAsync preserves whichever fields a given report didn't include (see
/// its own doc comment) rather than one report type clobbering the other's data.</summary>
public record StreamInfoReportItem(Guid CameraId, string StreamRole, int? Width, int? Height, string? Codec,
    int? Fps = null, int? BitrateKbps = null, int? ReconnectCount = null);
