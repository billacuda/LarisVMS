using System.Security.Claims;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Core.Interfaces;

/// <summary>Admin-editable site branding (app name, colors, logo, font), resolved and validated for
/// the shared layout. Reads fall back to the Setup wizard's setup-generated.json values until an
/// admin saves on Admin/Branding, so a fresh install looks exactly as it did before this existed.</summary>
public interface IBrandingService
{
    Task<BrandingOptions> GetAsync(CancellationToken ct = default);

    Task SaveAsync(BrandingOptions options, string? modifiedBy, CancellationToken ct = default);
}

/// <summary>Reads and writes the admin-configurable colours used by every timeline and event badge.</summary>
public interface IEventColorService
{
    Task<EventPalette> GetAsync(CancellationToken ct = default);

    Task SaveAsync(EventPalette palette, string? modifiedBy, CancellationToken ct = default);
}

public interface IDashboardService
{
    /// <summary>Per-camera health rows plus summary counts — the exact data Pages/Index renders on
    /// first load and GET /api/dashboard returns for its 60s AJAX refresh. Rows (and every count
    /// derived from them) are narrowed to cameras this principal holds CameraAccessActions.View for
    /// — a restricted principal's dashboard hides a camera entirely rather than merely gating its
    /// thumbnail, the same way the camera/playback list pages already hide theirs.</summary>
    Task<DashboardHealthDto> GetHealthAsync(ClaimsPrincipal user, CancellationToken ct = default);

    /// <summary>Every Node, for the M20 monitoring API — unlike GetHealthAsync's own node tally, not
    /// narrowed to nodes that currently have a camera assigned, and not CameraAccess-scoped (there's no
    /// per-node ACL anywhere in this app; Nodes.Edit gates the whole Admin Nodes page, not a subset of
    /// nodes within it).</summary>
    Task<List<NodeStatusRow>> GetAllNodeStatusAsync(CancellationToken ct = default);
}

/// <summary>Who is asking for dashboard widgets. The permission flags are checked by the caller (the
/// web tier owns authorization policies): CanViewAlerts gates the recent-alerts widget, CanOpenNodes
/// decides whether node rows link to the node's admin page.</summary>
public record DashboardViewer(ClaimsPrincipal User, bool CanViewAlerts, bool CanOpenNodes);

public interface IDashboardWidgetService
{
    /// <summary>The widgets this viewer may add to their dashboard.</summary>
    IReadOnlyList<DashboardWidgetDefinition> GetAvailable(DashboardViewer viewer);

    /// <summary>The top 10 rows of each requested widget, in request order. Unknown keys, keys the
    /// viewer may not use, and non-list widgets (summary, node cards, camera table) are skipped.</summary>
    Task<List<DashboardWidgetResult>> GetTopAsync(DashboardViewer viewer,
        IReadOnlyList<(string Key, DashboardWidgetOptions Options)> widgets, CancellationToken ct = default);

    /// <summary>One page of a widget's full report, filtered by free text (name, node, note) and
    /// optionally to one node, and optionally re-sorted by "name", "node" or "value". Null when the
    /// key is unknown or not available to this viewer.</summary>
    Task<DashboardReportPage?> GetReportAsync(DashboardViewer viewer, string key, DashboardWidgetOptions options,
        int page, int pageSize, string? query, Guid? nodeId, string? sort, bool descending, CancellationToken ct = default);
}
