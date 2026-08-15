using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages;

/// <summary>
/// M11 health dashboard: per-camera real-time signal (fps/bitrate/reconnects from CameraStream,
/// refreshed every ~15s by NodeWorker.EnqueueHealthReports) plus each owning node's own online
/// status. Ordered before Alerting in the M7/M8/M11 finish-up plan specifically so alerting has real
/// collected signals to trigger on instead of a second, duplicate collection path.
/// </summary>
[Authorize]
public class IndexModel(ICameraService cameraService) : PageModel
{
    // Same 2-minute staleness window Admin/Nodes already uses for a node's own online/offline badge
    // — kept in sync rather than each page inventing its own threshold.
    private static readonly TimeSpan NodeOnlineWindow = TimeSpan.FromMinutes(2);
    // 3x NodeWorker's 15s health-report tick — one missed cycle (a transient report failure,
    // immediately retried per FlushStreamInfoAsync) shouldn't flip a camera to "not reporting";
    // several in a row should.
    private static readonly TimeSpan HealthFreshWindow = TimeSpan.FromSeconds(45);

    public List<CameraHealthRow> Rows { get; set; } = [];
    public int RecordingCount { get; set; }
    public int NotReportingCount { get; set; }
    public int DisabledCount { get; set; }
    public int NodesOnlineCount { get; set; }
    public int NodesTotalCount { get; set; }

    public record CameraHealthRow(Guid CameraId, string CameraName, bool CameraEnabled,
        string? NodeName, bool NodeOnline, bool NodeAssigned,
        int? Fps, int? BitrateKbps, int? ReconnectCount, DateTime? HealthReportedAt, bool HealthFresh);

    public async Task OnGetAsync()
    {
        var cameras = await cameraService.ListAsync();
        var now = DateTime.UtcNow;

        Rows = cameras.Select(c =>
        {
            var main = c.Streams.FirstOrDefault(s => s.Role == CameraStreamRole.Main);
            var nodeOnline = c.Node?.LastSeenAt is { } seen && now - seen < NodeOnlineWindow;
            var healthFresh = main?.HealthReportedAt is { } reported && now - reported < HealthFreshWindow;

            return new CameraHealthRow(c.Id, c.Name, c.IsEnabled,
                c.Node?.Name, nodeOnline, c.NodeId is not null,
                main?.Fps, main?.BitrateKbps, main?.ReconnectCount, main?.HealthReportedAt, healthFresh);
        }).ToList();

        RecordingCount = Rows.Count(r => r.CameraEnabled && r.HealthFresh);
        NotReportingCount = Rows.Count(r => r.CameraEnabled && !r.HealthFresh);
        DisabledCount = Rows.Count(r => !r.CameraEnabled);

        var nodesSeen = cameras.Where(c => c.Node is not null)
            .Select(c => c.Node!)
            .DistinctBy(n => n.Id)
            .ToList();
        NodesTotalCount = nodesSeen.Count;
        NodesOnlineCount = nodesSeen.Count(n => n.LastSeenAt is { } seen && now - seen < NodeOnlineWindow);
    }
}
