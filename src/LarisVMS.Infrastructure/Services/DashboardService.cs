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
public class DashboardService(ICameraService cameraService, ICameraAccessService cameraAccess, INodeService nodeService) : IDashboardService
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
        var cameras = await cameraService.ListAsync(ct);

        // Filtered here, at the source, rather than after building rows — every computation below
        // (rows, the recording/not-reporting/disabled counts, and the online-node tally) derives
        // from `cameras`, so a restricted principal's summary numbers and node count also only ever
        // reflect what they can actually see, not the true system-wide totals.
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

        var nodesSeen = cameras.Where(c => c.Node is not null)
            .Select(c => c.Node!)
            .DistinctBy(n => n.Id)
            .ToList();
        var nodesTotalCount = nodesSeen.Count;
        var nodesOnlineCount = nodesSeen.Count(n => n.LastSeenAt is { } seen && now - seen < NodeOnlineWindow);

        return new DashboardHealthDto(rows, recordingCount, notReportingCount, disabledCount,
            nodesOnlineCount, nodesTotalCount);
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
