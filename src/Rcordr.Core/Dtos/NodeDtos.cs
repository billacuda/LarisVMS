namespace Rcordr.Core.Dtos;

// Wire DTOs for the node control plane (POST /api/nodes/*). Shared via Core so Rcordr.Node can
// serialize/deserialize them without referencing Rcordr.Infrastructure or Rcordr.Web.

public record NodeRegisterRequest(string RegistrationKey, string Hostname, string? Version, string? Platform);
public record NodeRegisterResponse(Guid NodeId, string Secret);

public record NodeHeartbeatRequest(string? Version);
public record NodeHeartbeatResponse(int IntervalSeconds);

public record NodeConfigStreamDto(Guid StreamId, string Role, string RtspUri,
    string? Codec, int? Width, int? Height, bool HasAudio);
public record NodeConfigCameraDto(Guid CameraId, string Name, string? Username, string? Password,
    List<NodeConfigStreamDto> Streams);
public record NodeConfigResponse(List<NodeConfigCameraDto> Cameras, string? StorageRootPath);

public record SegmentReportItem(Guid CameraId, string StreamRole, DateTime StartUtc, DateTime EndUtc,
    string FilePath, long SizeBytes, string? Codec, int? Width, int? Height, bool HasAudio);

public record NodeStatusReportItem(Guid CameraId, string State, DateTime? LastSegmentAt, string? Error);
