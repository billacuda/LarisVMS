using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Services;

/// <summary>M11 health dashboard data: per-camera real-time signal (fps/bitrate/reconnects from
/// CameraStream, refreshed every ~15s by NodeWorker.EnqueueHealthReports) plus each owning node's own
/// online status. Split out of Pages/Index.cshtml.cs's own OnGetAsync so the exact same computation
/// backs both the server-rendered initial page load and GET /api/dashboard's 60s AJAX refresh — the
/// two must never independently drift out of sync with each other.</summary>
public class DashboardService(ICameraService cameraService) : IDashboardService
{
    // Same 2-minute staleness window Admin/Nodes already uses for a node's own online/offline badge
    // — kept in sync rather than each page inventing its own threshold.
    private static readonly TimeSpan NodeOnlineWindow = TimeSpan.FromMinutes(2);
    // 3x NodeWorker's 15s health-report tick — one missed cycle (a transient report failure,
    // immediately retried per FlushStreamInfoAsync) shouldn't flip a camera to "not reporting";
    // several in a row should.
    private static readonly TimeSpan HealthFreshWindow = TimeSpan.FromSeconds(45);

    public async Task<DashboardHealthDto> GetHealthAsync(CancellationToken ct = default)
    {
        var cameras = await cameraService.ListAsync(ct);
        var now = DateTime.UtcNow;

        var rows = cameras.Select(c =>
        {
            var main = c.Streams.FirstOrDefault(s => s.Role == CameraStreamRole.Main);
            var nodeOnline = c.Node?.LastSeenAt is { } seen && now - seen < NodeOnlineWindow;
            var healthFresh = main?.HealthReportedAt is { } reported && now - reported < HealthFreshWindow;

            return new CameraHealthRow(c.Id, c.Name, c.IsEnabled,
                c.Node?.Name, nodeOnline, c.NodeId is not null,
                main?.Fps, main?.BitrateKbps, main?.ReconnectCount, main?.HealthReportedAt, healthFresh,
                main?.AudioCodec, main?.AudioSampleRateHz);
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
}
