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
