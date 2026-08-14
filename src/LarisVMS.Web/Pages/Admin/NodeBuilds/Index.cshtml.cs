using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.NodeBuilds;

/// <summary>Approval queue for recorder-node builds — same Nodes.Edit gate as Admin/Nodes, since
/// this is node administration too, just the "give nodes something new to run" half rather than the
/// "manage already-registered nodes" half. Registration itself happens outside the browser now (see
/// deploy.ps1's own node-build-registration step and INodeBuildService's doc comment); this page
/// only approves/rejects what's already landed as Pending.</summary>
[Authorize("Nodes.Edit")]
public class IndexModel(INodeBuildService nodeBuildService) : PageModel
{
    public List<NodeBuildVersion> Builds { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Builds = await nodeBuildService.ListAsync(ct);
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken ct)
    {
        var by = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        await nodeBuildService.ApproveAsync(id, by, ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken ct)
    {
        var by = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        await nodeBuildService.RejectAsync(id, by, ct);
        return RedirectToPage();
    }
}
