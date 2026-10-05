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
    int NodesOnlineCount, int NodesTotalCount,
    List<NodeDashboardRow>? Nodes = null);

/// <summary>A count over the dashboard's four look-back windows, each including the shorter ones.</summary>
public record WindowCounts(int LastHour, int Last24Hours, int Last7Days, int Last30Days);

/// <summary>One node's card on the dashboard. Host figures are the node machine's own load from its
/// latest heartbeat (HostStatsFresh false when that reading is older than the online window). TotalFps
/// sums the main-stream fps of the cameras it is recording right now. The detection counts are only
/// filled in when object detection is enabled on the node, and cover only cameras the viewer can see.</summary>
public record NodeDashboardRow(Guid NodeId, string NodeName, bool Online,
    double? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes,
    long? NetReceiveBytesPerSec, long? NetSendBytesPerSec, bool HostStatsFresh,
    int TotalFps, int CamerasReporting,
    bool ObjectDetectionEnabled, WindowCounts? Events, WindowCounts? Humans, WindowCounts? Vehicles, WindowCounts? Animals);

/// <summary>Detection spans for one camera grouped by kind and age (TimelineService.GetEventCountsAsync).
/// Kind: "Tag" (custom tag rule), "Ai" (AI detection, Name = category), "Camera" (the camera's own
/// analytics, Name = DetectionKind). Bucket 0 = last hour, 1 = last 24 h, 2 = last 7 days, 3 = last 30
/// days, each excluding the shorter ones. Objects sums each span's peak simultaneous count.</summary>
public record CameraEventCountRow(Guid CameraId, string Kind, string? Name, int Bucket, int Spans, int Objects);

/// <summary>One node's row for the M20 monitoring API — every Node, with its own reachability and
/// disk space.</summary>
public record NodeStatusRow(Guid NodeId, string NodeName, bool Online,
    long? StorageFreeBytes, long? StorageTotalBytes, string? Version, string? Platform);
