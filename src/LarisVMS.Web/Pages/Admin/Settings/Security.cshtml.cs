using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Auth;
using LarisVMS.Web.Middleware;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>M20 pass 2 added the two IP allow lists IpAllowListMiddleware enforces. Revived name, not
/// content — the previous Security tab's only setting (the live/playback custom port) moved onto the
/// Live View tab during the roles/permissions overhaul; this is a new, genuinely security-themed pair
/// of settings, not a resurrection of the old tab's content. M20 pass 3 added Entra ID sign-in
/// configuration alongside it.</summary>
[Authorize("Settings.Edit")]
public class SecurityModel(ISettingsResolver settings, ApplicationDbContext db,
    IOptionsMonitorCache<OpenIdConnectOptions> oidcOptionsCache, IAuditService auditService) : PageModel
{
    [BindProperty] public string ManagementAllowList { get; set; } = string.Empty;
    [BindProperty] public string LiveViewAllowList { get; set; } = string.Empty;

    [BindProperty] public bool EntraEnabled { get; set; }
    [BindProperty] public string? EntraTenantId { get; set; }
    [BindProperty] public string? EntraClientId { get; set; }
    /// <summary>Blank on save means "leave unchanged" — same convention EmailSettings' own secret
    /// fields use, since the real value is never round-tripped back to the browser.</summary>
    [BindProperty] public string? EntraClientSecret { get; set; }
    public bool HasStoredEntraClientSecret { get; set; }

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        ManagementAllowList = await settings.GetRawAsync(IpAllowListMiddleware.ManagementSettingKey) ?? string.Empty;
        LiveViewAllowList = await settings.GetRawAsync(IpAllowListMiddleware.LiveViewSettingKey) ?? string.Empty;

        var entra = await db.EntraSsoSettings.AsNoTracking().FirstOrDefaultAsync();
        EntraEnabled = entra?.IsEnabled ?? false;
        EntraTenantId = entra?.TenantId;
        EntraClientId = entra?.ClientId;
        HasStoredEntraClientSecret = !string.IsNullOrEmpty(entra?.ClientSecret);
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

        var entra = await db.EntraSsoSettings.FirstOrDefaultAsync();
        var hadStoredSecret = !string.IsNullOrEmpty(entra?.ClientSecret);
        if (EntraEnabled && (string.IsNullOrWhiteSpace(EntraTenantId) || string.IsNullOrWhiteSpace(EntraClientId)
            || (!hadStoredSecret && string.IsNullOrWhiteSpace(EntraClientSecret))))
        {
            ErrorMessage = "Enter a Tenant ID, Client ID, and Client Secret before enabling Entra sign-in.";
            HasStoredEntraClientSecret = hadStoredSecret;
            return Page();
        }

        var by = User.Identity?.Name;
        var oldManagement = await settings.GetRawAsync(IpAllowListMiddleware.ManagementSettingKey) ?? string.Empty;
        var oldLiveView = await settings.GetRawAsync(IpAllowListMiddleware.LiveViewSettingKey) ?? string.Empty;
        var oldEntraEnabled = entra?.IsEnabled ?? false;

        await settings.SetGlobalAsync(IpAllowListMiddleware.ManagementSettingKey, ManagementAllowList, by);
        await settings.SetGlobalAsync(IpAllowListMiddleware.LiveViewSettingKey, LiveViewAllowList, by);

        var isNewEntraRow = entra is null;
        entra ??= new EntraSsoSettings { Id = Guid.NewGuid() };
        entra.IsEnabled = EntraEnabled;
        entra.TenantId = string.IsNullOrWhiteSpace(EntraTenantId) ? null : EntraTenantId.Trim();
        entra.ClientId = string.IsNullOrWhiteSpace(EntraClientId) ? null : EntraClientId.Trim();
        if (!string.IsNullOrWhiteSpace(EntraClientSecret)) entra.ClientSecret = EntraClientSecret;
        entra.LastModifiedAt = DateTime.UtcNow;
        entra.LastModifiedBy = by;
        if (isNewEntraRow) db.EntraSsoSettings.Add(entra);
        await db.SaveChangesAsync();

        // Forces EntraOidcOptionsConfigurator to re-run on the next sign-in challenge instead of
        // reusing whatever it resolved (and IOptionsMonitorCache cached) last time — see its own doc
        // comment for why this is what makes a saved change take effect with no app restart needed.
        oidcOptionsCache.TryRemove(EntraOidcOptionsConfigurator.SchemeName);

        var details = AuditDiff.Build(
            AuditDiff.Of("Management/API allow list", oldManagement, ManagementAllowList),
            AuditDiff.Of("Live View/playback allow list", oldLiveView, LiveViewAllowList),
            AuditDiff.Of("Entra sign-in enabled", oldEntraEnabled.ToString(), EntraEnabled.ToString()),
            AuditDiff.SecretChanged("Entra client secret", !string.IsNullOrWhiteSpace(EntraClientSecret)));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        // Never echoed back — same "blank means unchanged, the real value never round-trips to the
        // browser" convention EmailSettings' own secret fields use.
        EntraClientSecret = null;
        HasStoredEntraClientSecret = !string.IsNullOrEmpty(entra.ClientSecret);

        SavedMessage = "Saved.";
        return Page();
    }
}
