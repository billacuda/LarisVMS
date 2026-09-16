using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>The Settings hub — replaces the old navbar "Admin" dropdown (which only ever linked to
/// this page) plus the top-level Nodes/Logs nav buttons, now that the sidebar has room for exactly
/// one "Settings" item instead. Every card below links to the same real, independently-[Authorize]d
/// page those used to; this page only decides which cards to *show*, using the identical
/// PermissionService.GetGrantedAsync gate Pages/Shared/_Layout.cshtml already computes for the
/// sidebar itself, so a card never appears for a destination the user can't actually open.
/// No [Authorize] of its own for the same reason Admin/Settings/Index and Logs/Index never had one:
/// visibility is entirely per-card, not per-page.</summary>
public class IndexModel(IPermissionService permissionService) : PageModel
{
    public record CardInfo(string Href, string Icon, string Title, string Description);

    public List<CardInfo> Cards { get; set; } = [];

    public async Task OnGetAsync()
    {
        var nav = await permissionService.GetGrantedAsync(User);
        var showSettings = nav.Has("Settings", "Edit");
        var all = new (string Href, string Icon, string Title, string Description, bool Visible)[]
        {
            ("/Cameras", "📷", "Cameras", "Add, edit, and stream-configure connected cameras.", nav.Has("Cameras", "View")),
            ("/Admin/Nodes", "🔌", "Nodes", "LarisNode NVR instances, capacity, and pairing.", nav.Has("Nodes", "Edit")),
            ("/Admin/NodeBuilds", "🏗️", "Node builds", "Approval queue for recorder-node builds.", nav.Has("Nodes", "Edit")),
            ("/Admin/Proxies", "🔗", "Media proxies", "Relay endpoints for remote and low-bandwidth streams.", nav.Has("Nodes", "Edit")),
            ("/Logs", "📜", "Logs", "System, audit, and diagnostic log streams.", nav.Has("Logs", "View") || nav.Has("SystemLogs", "View")),
            ("/Admin/Plugins", "🧩", "Plugins", "Installed camera integration providers and their versions.", nav.Has("Plugins", "View")),
            ("/Admin/Settings/Backups", "💾", "Backups", "Configuration snapshots and restore points.", nav.Has("Backups", "Edit")),
            ("/Admin/Settings/Branding", "🎨", "Branding", "Logo, colors, and login-screen customization.", showSettings),
            ("/Admin/Settings/Permissions/Users", "👤", "Users", "Accounts, invitations, and login history.", nav.Has("Users", "Edit")),
            ("/Admin/Settings/Permissions/Roles", "🛡️", "Roles", "Named role definitions assigned to users.", showSettings),
            ("/Admin/Settings/Permissions/Matrix", "🗂️", "Permissions matrix", "Cross-reference roles against permitted actions.", showSettings),
            ("/Admin/Settings/CameraAccess", "🗝️", "Camera access", "Per-role/user camera and group visibility.", showSettings),
            ("/Admin/Settings/Detection", "🤖", "AI detection", "Detection models, zones, and per-node engine settings.", showSettings),
            ("/Admin/Settings/Email", "✉️", "Email", "SMTP relay and notification templates.", showSettings),
            ("/Admin/Settings/Events", "🏷️", "Events", "Event-tag colors and rule defaults.", showSettings),
            ("/Admin/Settings/LiveView", "🖥️", "Live view", "Default grid layout, overlays, and stream quality.", showSettings),
            ("/Admin/Settings/Logs", "📝", "Log settings", "Retention, verbosity, and remote log forwarding.", showSettings),
            ("/Admin/Settings/Nodes", "🖧", "Node settings", "Per-node defaults for new LarisNode instances.", showSettings),
            ("/Admin/Settings/Recording", "🎥", "Recording settings", "Codec, bitrate, and continuous vs. motion recording.", showSettings),
            ("/Admin/Settings/Security", "🔒", "Security settings", "IP allow lists and live/playback port configuration.", showSettings),
            ("/Admin/Settings/StorageAndRetention", "🗄️", "Storage & retention", "Disk pools, retention windows, and archiving.", nav.Has("Retention", "Edit")),
            ("/Admin/Alerts", "⚠️", "Alerts", "Alert rules, channels, and thresholds.", nav.Has("Alerts", "Edit")),
            ("/Admin/ApiKeys", "🔑", "API keys", "Issue and revoke integration credentials.", nav.Has("ApiKeys", "Edit")),
        };
        Cards = [.. all.Where(c => c.Visible).Select(c => new CardInfo(c.Href, c.Icon, c.Title, c.Description))];
    }
}
