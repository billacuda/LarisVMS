using System.Security.Claims;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Services;

/// <summary>M11 health dashboard data: per-camera real-time signal (fps/bitrate/reconnects from
/// CameraStream, refreshed every ~15s by NodeWorker.EnqueueHealthReports) plus each owning node's own
/// online status. Split out of Pages/Index.cshtml.cs's own OnGetAsync so the exact same computation
/// backs both the server-rendered initial page load and GET /api/dashboard's 60s AJAX refresh — the
/// two must never independently drift out of sync with each other.</summary>
public class DashboardService(ICameraService cameraService, ICameraAccessService cameraAccess, INodeService nodeService,
    ITimelineService? timeline = null) : IDashboardService
{
    // Same 2-minute staleness window Admin/Nodes already uses for a node's own online/offline badge
    // — kept in sync rather than each page inventing its own threshold. Public: AlertEvaluationPolicy
    // (LarisVMS.Web.Services) reuses these exact windows so "offline"/"not reporting" mean the same
    // thing on the Dashboard and in an alert firing, rather than two thresholds silently drifting.
    public static readonly TimeSpan NodeOnlineWindow = TimeSpan.FromMinutes(2);
    // 3x NodeWorker's 15s health-report tick — one missed cycle (a transient report failure,
    // immediately retried per FlushStreamInfoAsync) shouldn't flip a camera to "not reporting";
    // several in a row should.
    public static readonly TimeSpan HealthFreshWindow = TimeSpan.FromSeconds(45);

    public async Task<DashboardHealthDto> GetHealthAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var allCameras = await cameraService.ListAsync(ct);
        var cameras = allCameras;

        // Filtered here, at the source, rather than after building rows — the rows and the
        // recording/not-reporting/disabled counts derive from `cameras`, so a restricted principal's
        // camera numbers only reflect what they can see. The node tally below is system-wide.
        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(user, CameraAccessActions.View, ct);
        if (accessible is not null) cameras = cameras.Where(c => accessible.Contains(c.Id)).ToList();

        var now = DateTime.UtcNow;

        var rows = cameras.Select(c =>
        {
            var main = c.Streams.FirstOrDefault(s => s.Role == CameraStreamRole.Main);
            var nodeOnline = c.Node?.LastSeenAt is { } seen && now - seen < NodeOnlineWindow;
            var healthFresh = main?.HealthReportedAt is { } reported && now - reported < HealthFreshWindow;

            return new CameraHealthRow(c.Id, c.Name, c.IsEnabled,
                c.Node?.Name, nodeOnline, c.NodeId is not null,
                main?.Fps, main?.BitrateKbps, main?.ReconnectCount, main?.HealthReportedAt, healthFresh,
                main?.AudioCodec, main?.AudioSampleRateHz,
                main?.IsEngineBuilding, main?.EngineBuildFailed);
        }).ToList();

        var recordingCount = rows.Count(r => r.CameraEnabled && r.HealthFresh);
        var notReportingCount = rows.Count(r => r.CameraEnabled && !r.HealthFresh);
        var disabledCount = rows.Count(r => !r.CameraEnabled);

        // Every registered node counts, including ones with no cameras — a node with nothing assigned
        // is still a box that should be reporting. Pending (unapproved) and Disabled nodes are excluded.
        var nodes = (await nodeService.ListAsync(ct))
            .Where(n => n.Status is not (NodeStatus.Pending or NodeStatus.Disabled))
            .ToList();
        var nodesTotalCount = nodes.Count;
        var nodesOnlineCount = nodes.Count(n => n.LastSeenAt is { } seen && now - seen < NodeOnlineWindow);

        var nodeRows = await BuildNodeRowsAsync(nodes, allCameras, cameras, accessible, now, ct);

        return new DashboardHealthDto(rows, recordingCount, notReportingCount, disabledCount,
            nodesOnlineCount, nodesTotalCount, nodeRows);
    }

    /// <summary>One card per node. Host load is system-wide (like the nodes-online tile); fps and the
    /// detection counts only cover the cameras the viewer can see.
    ///
    /// Counting rules, so one object isn't counted twice by two sources: a camera running AI detection
    /// contributes its AI detections, a camera without it contributes its own analytics events, and
    /// custom tags always count. Humans/vehicles/animals add up each detection's peak simultaneous
    /// count (two people walking by together count as two). Events are attributed to the camera's
    /// home node.</summary>
    private async Task<List<NodeDashboardRow>> BuildNodeRowsAsync(List<Core.Entities.Node> nodes,
        List<Core.Entities.Camera> allCameras, List<Core.Entities.Camera> visibleCameras,
        IReadOnlySet<Guid>? accessible, DateTime now, CancellationToken ct)
    {
        var failoverStateOf = nodes.ToDictionary(n => n.Id, n => n.FailoverState);
        var defaultBackupOf = nodes.ToDictionary(n => n.Id, n => n.BackupNodeId);
        var disabledAiByNode = nodes.ToDictionary(n => n.Id, n => n.DisableAiObjectDetection);
        bool AiOn(Core.Entities.Camera c) =>
            c.AiDetectionEnabled && c.NodeId is { } home && !disabledAiByNode.GetValueOrDefault(home);

        var counts = timeline is null ? [] : await timeline.GetEventCountsAsync(accessible, now, ct);
        var visibleById = visibleCameras.ToDictionary(c => c.Id);

        var result = new List<NodeDashboardRow>();
        foreach (var node in nodes)
        {
            var online = node.LastSeenAt is { } seen && now - seen < NodeOnlineWindow;
            var hostFresh = node.HostStatsUpdatedAt is { } at && now - at < NodeOnlineWindow;

            var recording = visibleCameras.Where(c => c.IsEnabled && RecordingNodeResolver.Resolve(
                c.NodeId, c.BackupNodeIdOverride, failoverStateOf, defaultBackupOf) == node.Id).ToList();
            var fresh = recording
                .Select(c => c.Streams.FirstOrDefault(s => s.Role == CameraStreamRole.Main))
                .Where(s => s?.HealthReportedAt is { } r && now - r < HealthFreshWindow)
                .ToList();

            var detectionOn = !node.DisableAiObjectDetection && allCameras.Any(c => c.NodeId == node.Id && c.AiDetectionEnabled);

            WindowCounts? events = null, humans = null, vehicles = null, animals = null;
            if (detectionOn)
            {
                var nodeCounts = counts.Where(r => visibleById.TryGetValue(r.CameraId, out var cam) && cam.NodeId == node.Id
                    && (r.Kind == "Tag" || (r.Kind == "Ai") == AiOn(cam))).ToList();
                events = Windows(nodeCounts, _ => true, objects: false);
                humans = Windows(nodeCounts, r => r.Name is "Human" or nameof(DetectionKind.Human) or nameof(DetectionKind.Face), objects: true);
                vehicles = Windows(nodeCounts, r => r.Name is "Vehicle" or nameof(DetectionKind.Vehicle), objects: true);
                animals = Windows(nodeCounts, r => r.Name is "Animal" or nameof(DetectionKind.Animal), objects: true);
            }

            result.Add(new NodeDashboardRow(node.Id, node.Name, online,
                hostFresh ? node.CpuPercent : null, hostFresh ? node.MemoryUsedBytes : null, hostFresh ? node.MemoryTotalBytes : null,
                hostFresh ? node.NetReceiveBytesPerSec : null, hostFresh ? node.NetSendBytesPerSec : null, hostFresh,
                fresh.Sum(s => s!.Fps ?? 0), fresh.Count,
                detectionOn, events, humans, vehicles, animals));
        }
        return result;
    }

    /// <summary>Cumulative counts per window from CameraEventCountRow's exclusive age buckets: spans for
    /// events, peak-object totals for object classes (custom tags never count as an object).</summary>
    internal static WindowCounts Windows(IEnumerable<CameraEventCountRow> rows, Func<CameraEventCountRow, bool> include, bool objects)
    {
        var perBucket = new int[4];
        foreach (var r in rows)
        {
            if (!include(r) || r.Bucket is < 0 or > 3) continue;
            if (objects && r.Kind == "Tag") continue;
            perBucket[r.Bucket] += objects ? r.Objects : r.Spans;
        }
        return new WindowCounts(perBucket[0], perBucket[0] + perBucket[1],
            perBucket[0] + perBucket[1] + perBucket[2], perBucket.Sum());
    }

    public async Task<List<NodeStatusRow>> GetAllNodeStatusAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var nodes = await nodeService.ListAsync(ct);
        return nodes.Select(n => new NodeStatusRow(n.Id, n.Name,
            n.LastSeenAt is { } seen && now - seen < NodeOnlineWindow,
            n.StorageFreeBytes, n.StorageTotalBytes, n.Version, n.Platform)).ToList();
    }
}
