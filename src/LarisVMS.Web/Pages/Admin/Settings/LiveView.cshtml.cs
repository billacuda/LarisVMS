using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Middleware;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>M18: the one global toggle for adaptive streaming — see NodeConfigResponse.
/// AdaptiveStreamingEnabled's doc comment for why this is global-only (no per-node/per-camera
/// override) and why the node itself is the authoritative gate, not just a client-side hint.
///
/// Also carries the live/playback custom-port setting (PortSegmentationMiddleware), relocated here
/// from the retired Security tab as part of the roles/permissions overhaul's page consolidation —
/// it's specifically about live/playback traffic, which is what this tab is for.</summary>
[Authorize("Settings.Edit")]
public class LiveViewModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public bool AdaptiveStreamingEnabled { get; set; } = false;

    /// <summary>Null/0 (the default) means every route stays reachable on whatever port(s) IIS
    /// already binds — the feature only starts separating traffic once an admin sets a real port
    /// here <em>and</em> adds a matching IIS site binding for it (this setting alone can't open a
    /// new listening socket under IIS in-process hosting).</summary>
    [BindProperty] public int? CustomPort { get; set; }

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        AdaptiveStreamingEnabled = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", false);
        CustomPort = await settings.GetAsync<int?>(PortSegmentationMiddleware.SettingKey, null);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (CustomPort is { } p && (p is < 1 or > 65535))
        {
            ErrorMessage = "The custom port must be between 1 and 65535, or blank to disable it.";
            return Page();
        }

        var by = User.Identity?.Name;
        var oldValue = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", false);
        var oldPort = await settings.GetAsync<int?>(PortSegmentationMiddleware.SettingKey, null);

        await settings.SetGlobalAsync("LiveView.AdaptiveStreamingEnabled", AdaptiveStreamingEnabled.ToString(), by);
        await settings.SetGlobalAsync(PortSegmentationMiddleware.SettingKey, CustomPort?.ToString() ?? "", by);

        var details = AuditDiff.Build(
            AuditDiff.Of("LiveView.AdaptiveStreamingEnabled", oldValue.ToString(), AdaptiveStreamingEnabled.ToString()),
            AuditDiff.Of("Live/playback custom port", oldPort?.ToString() ?? "(none)", CustomPort?.ToString() ?? "(none)"));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
