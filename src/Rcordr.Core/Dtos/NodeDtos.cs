namespace Rcordr.Core.Dtos;

// Wire DTOs for the node control plane (POST /api/nodes/*). Shared via Core so Rcordr.Node can
// serialize/deserialize them without referencing Rcordr.Infrastructure or Rcordr.Web.

public record NodeRegisterRequest(string RegistrationKey, string Hostname, string? Version, string? Platform);
public record NodeRegisterResponse(Guid NodeId, string Secret);

public record NodeHeartbeatRequest(string? Version, long? FreeBytes = null, long? TotalBytes = null);
public record NodeHeartbeatResponse(int IntervalSeconds);

public record NodeConfigStreamDto(Guid StreamId, string Role, string RtspUri,
    string? Codec, int? Width, int? Height, bool HasAudio);
public record NodeConfigCameraDto(Guid CameraId, string Name, string? Username, string? Password,
    List<NodeConfigStreamDto> Streams, int? RetentionDays, long? QuotaBytes);
public record NodeConfigResponse(List<NodeConfigCameraDto> Cameras, string? StorageRootPath, int WatermarkPercent);

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
