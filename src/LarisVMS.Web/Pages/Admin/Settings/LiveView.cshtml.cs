using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>M18: the one global toggle for adaptive streaming — see NodeConfigResponse.
/// AdaptiveStreamingEnabled's doc comment for why this is global-only (no per-node/per-camera
/// override) and why the node itself is the authoritative gate, not just a client-side hint.</summary>
[Authorize("Settings.Edit")]
public class LiveViewModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public bool AdaptiveStreamingEnabled { get; set; } = true;

    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        AdaptiveStreamingEnabled = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", true);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;
        var oldValue = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", true);

        await settings.SetGlobalAsync("LiveView.AdaptiveStreamingEnabled", AdaptiveStreamingEnabled.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("LiveView.AdaptiveStreamingEnabled", oldValue.ToString(), AdaptiveStreamingEnabled.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
