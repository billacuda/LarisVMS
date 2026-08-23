using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Middleware;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>M20 pass 2: the two IP allow lists IpAllowListMiddleware enforces. Revived name, not
/// content — the previous Security tab's only setting (the live/playback custom port) moved onto the
/// Live View tab during the roles/permissions overhaul; this is a new, genuinely security-themed pair
/// of settings, not a resurrection of the old tab's content.</summary>
[Authorize("Settings.Edit")]
public class SecurityModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public string ManagementAllowList { get; set; } = string.Empty;
    [BindProperty] public string LiveViewAllowList { get; set; } = string.Empty;

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        ManagementAllowList = await settings.GetRawAsync(IpAllowListMiddleware.ManagementSettingKey) ?? string.Empty;
        LiveViewAllowList = await settings.GetRawAsync(IpAllowListMiddleware.LiveViewSettingKey) ?? string.Empty;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var management = IpAllowListPolicy.Parse(ManagementAllowList);
        var liveView = IpAllowListPolicy.Parse(LiveViewAllowList);
        var invalid = management.InvalidLines.Concat(liveView.InvalidLines).ToList();
        if (invalid.Count > 0)
        {
            ErrorMessage = $"Not a valid IP or CIDR block: {string.Join(", ", invalid)}";
            return Page();
        }

        // This page is itself management traffic — refuse a management-list save that would lock out
        // the very request making it, rather than the documented-but-unhelpful "no built-in recovery"
        // outcome. Only the management list can do this to this page; the live/playback list has no
        // bearing on reaching Admin > Settings at all.
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (!IpAllowListPolicy.IsAllowed(remoteIp, management.Networks))
        {
            ErrorMessage = $"Not saved — the management/API list you entered would block your own address ({remoteIp}). Add it to the list first.";
            return Page();
        }

        var by = User.Identity?.Name;
        var oldManagement = await settings.GetRawAsync(IpAllowListMiddleware.ManagementSettingKey) ?? string.Empty;
        var oldLiveView = await settings.GetRawAsync(IpAllowListMiddleware.LiveViewSettingKey) ?? string.Empty;

        await settings.SetGlobalAsync(IpAllowListMiddleware.ManagementSettingKey, ManagementAllowList, by);
        await settings.SetGlobalAsync(IpAllowListMiddleware.LiveViewSettingKey, LiveViewAllowList, by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Management/API allow list", oldManagement, ManagementAllowList),
            AuditDiff.Of("Live View/playback allow list", oldLiveView, LiveViewAllowList));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
