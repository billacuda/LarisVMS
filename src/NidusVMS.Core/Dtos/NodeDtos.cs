namespace NidusVMS.Core.Dtos;

// Wire DTOs for the node control plane (POST /api/nodes/*). Shared via Core so NidusVMS.Node can
// serialize/deserialize them without referencing NidusVMS.Infrastructure or NidusVMS.Web.

public record NodeRegisterRequest(string RegistrationKey, string Hostname, string? Version, string? Platform);
public record NodeRegisterResponse(Guid NodeId, string Secret, string MediaSigningKey);

public record NodeHeartbeatRequest(string? Version, long? FreeBytes = null, long? TotalBytes = null, int? LivePort = null);
public record NodeHeartbeatResponse(int IntervalSeconds);

public record NodeConfigStreamDto(Guid StreamId, string Role, string RtspUri,
    string? Codec, int? Width, int? Height, bool HasAudio);

/// <summary>M8: only ServerMotion/Ignore zones are ever sent here — CameraMotion/Privacy don't
/// drive anything on the node (CameraMotion push happens from NidusVMS.Web over ONVIF in a
/// follow-up pass; Privacy has no node-side effect yet at all), so there's no reason to hand a
/// recorder node polygon data it can't act on.</summary>
public record NodeConfigZoneDto(Guid ZoneId, string Kind, string PolygonJson, double Sensitivity);

/// <summary>RecordingMode is "Continuous" or "Motion" (see ISettingsResolver's Recording.Mode key) —
/// only these two exist as of M8; Schedule/Event from the plan's original four-mode design remain
/// unbuilt (no Schedules table). MotionPreRollSeconds/MotionPostRollSeconds
/// (M8 pass 3) are how far before a motion event started, and after it ended, a segment still counts
/// as worth keeping for a Motion-mode camera — both irrelevant, and unused by the node, for a
/// Continuous camera. Kept as two separate values (not one shared "padding") because pre-roll and
/// post-roll are answering genuinely different questions and an operator may reasonably want them
/// different — a long pre-roll costs nothing extra (recording is already continuous either way) but
/// a long post-roll means keeping more low-signal footage per event.
///
/// EventsServiceUri (M8 pass 6): the node's first reason to make its own ONVIF SOAP calls rather
/// than only receiving ready-made RTSP URIs — ONVIF PullPoint event polling is a continuous,
/// long-lived conversation the web tier can't pre-resolve into a one-shot value the way
/// GetStreamUri's lookup already is. Still pre-*resolved*, though: this is the Events service's own
/// XAddr (from the capability prober's raw category map, CameraCapabilities.RawProbeJson —
/// deliberately NOT the same as Camera.DeviceServiceUri, ONVIF's device-management entry point),
/// handed over directly so the node never needs its own GetCapabilities round trip just to find out
/// where to send CreatePullPointSubscription. Null for a camera with no advertised Events service.</summary>
public record NodeConfigCameraDto(Guid CameraId, string Name, string? Username, string? Password,
    List<NodeConfigStreamDto> Streams, int? RetentionDays, long? QuotaBytes, List<NodeConfigZoneDto> Zones,
    string RecordingMode, int MotionPreRollSeconds, int MotionPostRollSeconds,
    string? EventsServiceUri);
public record NodeConfigResponse(List<NodeConfigCameraDto> Cameras, string? StorageRootPath, int WatermarkPercent, string MediaSigningKey);

/// <summary>One completed MotionSpan, batch-reported the same way SegmentReportItem is — see
/// NodeService.RecordMotionSpansAsync for why plain REST + EF insert is enough here despite the
/// plan flagging SqlBulkCopy for this table: a span is only written once it *closes* (debounced by
/// MotionHysteresis), not per frame, so real write volume looks like Segments' (a handful of rows
/// per camera per interesting event), not per-frame Detections-scale volume.
///
/// ZoneId is null for a camera-pushed (M8 pass 6) span — an ONVIF PullPoint event has no concept of
/// one of our own drawn ServerMotion zones — and non-null for a ServerMotion span; NodeService
/// infers Source from that instead of carrying a separate field.</summary>
public record MotionSpanReportItem(Guid CameraId, Guid? ZoneId, DateTime StartUtc, DateTime EndUtc, double Score);

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

/// <summary>Real resolution/codec parsed from ffmpeg's own stderr when it opens a camera's stream —
/// sent once per ffmpeg (re)start, more trustworthy than ONVIF's advertised
/// VideoEncoderConfiguration, which some cameras omit entirely. Only ever reported for the Main
/// stream today, since that's the only one the node actually opens with ffmpeg (Sub/Third aren't
/// consumed by anything until M5's live view).</summary>
public record StreamInfoReportItem(Guid CameraId, string StreamRole, int Width, int Height, string? Codec);
