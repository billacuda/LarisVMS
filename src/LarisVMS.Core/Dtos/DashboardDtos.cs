namespace LarisVMS.Core.Dtos;

/// <summary>One camera's row on the health dashboard — shared shape between the server-rendered
/// initial page load (Pages/Index) and its 60s AJAX refresh (GET /api/dashboard), so the two can
/// never drift out of sync with each other.</summary>
/// AudioCodec/AudioSampleRateHz are a static property of the stream (reported once per ffmpeg
/// connection), not a live measurement like Fps/BitrateKbps — so unlike those two they are shown
/// regardless of HealthFresh, as the last known truth about what the camera sends rather than a
/// number that would be misleading if stale.
public record CameraHealthRow(Guid CameraId, string CameraName, bool CameraEnabled,
    string? NodeName, bool NodeOnline, bool NodeAssigned,
    int? Fps, int? BitrateKbps, int? ReconnectCount, DateTime? HealthReportedAt, bool HealthFresh,
    string? AudioCodec, int? AudioSampleRateHz,
    /// <summary>This camera's AI-detection engine build state (Section H) — see
    /// <c>CameraStream.IsEngineBuilding</c>'s own doc comment. Both null/false means "nothing to
    /// report" (AI detection isn't running for this camera, or an older node build never sent it);
    /// dashboard.js renders a spinner or failure badge only when one is true. Orthogonal to recording
    /// health above — a camera can be recording fine while its detection engine is still cold-building
    /// a TensorRT engine.</summary>
    bool? IsEngineBuilding = null, bool? EngineBuildFailed = null);

public record DashboardHealthDto(List<CameraHealthRow> Rows,
    int RecordingCount, int NotReportingCount, int DisabledCount,
    int NodesOnlineCount, int NodesTotalCount);

/// <summary>One node's row for the M20 monitoring API — every Node, unlike
/// DashboardHealthDto's own node tally (which only ever counts nodes that currently have at least one
/// camera assigned, right for a camera-health summary but wrong for "is this node itself reachable and
/// does it have disk space").</summary>
public record NodeStatusRow(Guid NodeId, string NodeName, bool Online,
    long? StorageFreeBytes, long? StorageTotalBytes, string? Version, string? Platform);
