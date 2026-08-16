using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages;

/// <summary>
/// M11 health dashboard. The actual per-camera/summary computation lives in IDashboardService,
/// shared with GET /api/dashboard (this page's own 60s AJAX refresh, dashboard.js) so first load and
/// refresh can never independently drift out of sync. Ordered before Alerting in the M7/M8/M11
/// finish-up plan specifically so alerting has real collected signals to trigger on instead of a
/// second, duplicate collection path.
/// </summary>
[Authorize]
public class IndexModel(IDashboardService dashboardService) : PageModel
{
    public List<CameraHealthRow> Rows { get; set; } = [];
    public int RecordingCount { get; set; }
    public int NotReportingCount { get; set; }
    public int DisabledCount { get; set; }
    public int NodesOnlineCount { get; set; }
    public int NodesTotalCount { get; set; }

    public async Task OnGetAsync()
    {
        var health = await dashboardService.GetHealthAsync();
        Rows = health.Rows;
        RecordingCount = health.RecordingCount;
        NotReportingCount = health.NotReportingCount;
        DisabledCount = health.DisabledCount;
        NodesOnlineCount = health.NodesOnlineCount;
        NodesTotalCount = health.NodesTotalCount;
    }
}
