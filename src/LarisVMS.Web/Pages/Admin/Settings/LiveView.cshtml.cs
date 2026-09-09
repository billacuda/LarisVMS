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

    /// <summary>Failover plan phase 1. "Proxy" (default) = live/playback bytes relay through this
    /// server, as they always have. "Direct" = browsers connect straight to each recorder node's own
    /// HTTPS endpoint, skipping the central hop. Per-node overridable on Admin → Nodes. Falls back to
    /// proxy automatically for any camera whose node hasn't reported a healthy client endpoint.</summary>
    [BindProperty] public string DirectStreaming { get; set; } = "Proxy";

    /// <summary>Failover plan phase 1. Lets a node's client HTTPS endpoint come up on an
    /// auto-generated self-signed certificate (no real pfx) — setup/testing only; viewers must click
    /// through a browser warning, and every ticket + the Admin node row flags the stream insecure.</summary>
    [BindProperty] public bool AllowInsecureClientEndpoint { get; set; }

    /// <summary>Failover plan phase 1, optional. The public origin (scheme://host) browsers load this
    /// app from — echoed to nodes so they can pin it as the sole allowed CORS origin on their
    /// client-facing routes. Leave blank and each node simply reflects the request Origin instead
    /// (those routes are token-authorized regardless).</summary>
    [BindProperty] public string? PublicOrigin { get; set; }

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
        DirectStreaming = await settings.GetAsync("LiveView.DirectStreaming", "Proxy");
        AllowInsecureClientEndpoint = await settings.GetAsync("LiveView.AllowInsecureClientEndpoint", false);
        PublicOrigin = await settings.GetAsync<string?>("LiveView.PublicOrigin", null);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (CustomPort is { } p && (p is < 1 or > 65535))
        {
            ErrorMessage = "The custom port must be between 1 and 65535, or blank to disable it.";
            return Page();
        }
        DirectStreaming = string.Equals(DirectStreaming, "Direct", StringComparison.OrdinalIgnoreCase) ? "Direct" : "Proxy";
        PublicOrigin = string.IsNullOrWhiteSpace(PublicOrigin) ? null : PublicOrigin.Trim().TrimEnd('/');
        if (PublicOrigin is not null && !Uri.TryCreate(PublicOrigin, UriKind.Absolute, out _))
        {
            ErrorMessage = "Public origin must be a full URL like https://vms.example.com, or blank.";
            return Page();
        }

        var by = User.Identity?.Name;
        var oldValue = await settings.GetAsync("LiveView.AdaptiveStreamingEnabled", false);
        var oldPort = await settings.GetAsync<int?>(PortSegmentationMiddleware.SettingKey, null);
        var oldDirect = await settings.GetAsync("LiveView.DirectStreaming", "Proxy");
        var oldInsecure = await settings.GetAsync("LiveView.AllowInsecureClientEndpoint", false);
        var oldOrigin = await settings.GetAsync<string?>("LiveView.PublicOrigin", null);

        await settings.SetGlobalAsync("LiveView.AdaptiveStreamingEnabled", AdaptiveStreamingEnabled.ToString(), by);
        await settings.SetGlobalAsync(PortSegmentationMiddleware.SettingKey, CustomPort?.ToString() ?? "", by);
        await settings.SetGlobalAsync("LiveView.DirectStreaming", DirectStreaming, by);
        await settings.SetGlobalAsync("LiveView.AllowInsecureClientEndpoint", AllowInsecureClientEndpoint.ToString(), by);
        await settings.SetGlobalAsync("LiveView.PublicOrigin", PublicOrigin ?? "", by);

        var details = AuditDiff.Build(
            AuditDiff.Of("LiveView.AdaptiveStreamingEnabled", oldValue.ToString(), AdaptiveStreamingEnabled.ToString()),
            AuditDiff.Of("Live/playback custom port", oldPort?.ToString() ?? "(none)", CustomPort?.ToString() ?? "(none)"),
            AuditDiff.Of("Direct streaming", oldDirect, DirectStreaming),
            AuditDiff.Of("Allow insecure client endpoint", oldInsecure.ToString(), AllowInsecureClientEndpoint.ToString()),
            AuditDiff.Of("Public origin", oldOrigin ?? "(none)", PublicOrigin ?? "(none)"));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
