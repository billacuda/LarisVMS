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
public class IndexModel(INodeBuildService nodeBuildService, IAuditService auditService) : PageModel
{
    public List<NodeBuildVersion> Builds { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        // deploy.ps1 inserts Pending rows straight into the table, so tidy the queue on every visit
        // rather than only when the app registers a build itself.
        await nodeBuildService.SupersedeOutdatedPendingAsync(ct);
        Builds = await nodeBuildService.ListAsync(ct);
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id, CancellationToken ct)
    {
        var by = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        var build = (await nodeBuildService.ListAsync(ct)).FirstOrDefault(b => b.Id == id);
        try
        {
            await nodeBuildService.ApproveAsync(id, by, ct);
        }
        catch (InvalidOperationException ex)
        {
            // Superseded or rejected since the page was loaded (e.g. a newer build landed).
            ErrorMessage = ex.Message;
            await OnGetAsync(ct);
            return Page();
        }
        await LogAsync("NodeBuild.Approve", build, id, ct);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id, CancellationToken ct)
    {
        var by = User.FindFirst(ClaimTypes.Name)?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "unknown";
        var build = (await nodeBuildService.ListAsync(ct)).FirstOrDefault(b => b.Id == id);
        await nodeBuildService.RejectAsync(id, by, ct);
        await LogAsync("NodeBuild.Reject", build, id, ct);
        return RedirectToPage();
    }

    /// <summary>Approving a build is a fleet-wide action — every node picks it up on its next
    /// heartbeat — and had no audit coverage at all before this. The build is identified by
    /// version/platform rather than only its id, since that's what an operator reading the log
    /// months later actually recognizes.</summary>
    private Task LogAsync(string action, NodeBuildVersion? build, Guid id, CancellationToken ct) =>
        auditService.LogAsync(action,
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            build is null ? id.ToString() : $"{build.Version} ({build.Platform})", ct);
}
