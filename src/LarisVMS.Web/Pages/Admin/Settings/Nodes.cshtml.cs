using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>
/// Node registration key and the fleet-wide auto-update toggle. The actual node build approval
/// queue stays at its own Admin/NodeBuilds route rather than being embedded here: it's gated by
/// Nodes.Edit, a different permission than the rest of this page's Settings.Edit, and folding a
/// second permission requirement into one page would either 403 a Nodes.Edit-only operator who could
/// reach it today, or grant Settings.Edit-only operators an approve/reject action they shouldn't
/// have. This tab only shows a live pending-count summary and a link, which needs no permission
/// merge to be useful for discoverability.
/// </summary>
[Authorize("Settings.Edit")]
public class NodesModel(ISettingsResolver settings, INodeBuildService nodeBuildService, IAuditService auditService) : PageModel
{
    [BindProperty] public string? RegistrationKey { get; set; }
    [BindProperty] public bool NodeAutoUpdateEnabled { get; set; } = true;

    public int PendingBuildCount { get; set; }
    public string? SavedMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        RegistrationKey = await settings.GetRawAsync("Node.RegistrationKey");
        NodeAutoUpdateEnabled = await settings.GetAsync("NodeAutoUpdate.Enabled", true);
        PendingBuildCount = (await nodeBuildService.ListAsync(ct)).Count(b => b.Status == NodeBuildStatus.Pending);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var by = User.Identity?.Name;

        // Registration key is diffed as a secret — the entry records that it changed, never the key
        // itself, since the audit log is readable by anyone with Logs.View.
        var oldRegistrationKey = await settings.GetRawAsync("Node.RegistrationKey");
        var oldAutoUpdate = await settings.GetAsync("NodeAutoUpdate.Enabled", true);

        // Blank means "leave unchanged" — the form always submits the current key, so blank only
        // happens if a caller deliberately omits it.
        if (!string.IsNullOrWhiteSpace(RegistrationKey))
            await settings.SetGlobalAsync("Node.RegistrationKey", RegistrationKey, by);
        await settings.SetGlobalAsync("NodeAutoUpdate.Enabled", NodeAutoUpdateEnabled.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Secret("Node.RegistrationKey", oldRegistrationKey,
                string.IsNullOrWhiteSpace(RegistrationKey) ? oldRegistrationKey : RegistrationKey),
            AuditDiff.Of("NodeAutoUpdate.Enabled", oldAutoUpdate.ToString(), NodeAutoUpdateEnabled.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        PendingBuildCount = (await nodeBuildService.ListAsync(ct)).Count(b => b.Status == NodeBuildStatus.Pending);
        SavedMessage = "Saved.";
        return Page();
    }
}
