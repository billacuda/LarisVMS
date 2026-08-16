namespace LarisVMS.Core.Dtos;

/// <summary>One camera's row on the health dashboard — shared shape between the server-rendered
/// initial page load (Pages/Index) and its 60s AJAX refresh (GET /api/dashboard), so the two can
/// never drift out of sync with each other.</summary>
public record CameraHealthRow(Guid CameraId, string CameraName, bool CameraEnabled,
    string? NodeName, bool NodeOnline, bool NodeAssigned,
    int? Fps, int? BitrateKbps, int? ReconnectCount, DateTime? HealthReportedAt, bool HealthFresh);

public record DashboardHealthDto(List<CameraHealthRow> Rows,
    int RecordingCount, int NotReportingCount, int DisabledCount,
    int NodesOnlineCount, int NodesTotalCount);
