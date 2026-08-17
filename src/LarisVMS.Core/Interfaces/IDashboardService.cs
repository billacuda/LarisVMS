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
    /// first load and GET /api/dashboard returns for its 60s AJAX refresh.</summary>
    Task<DashboardHealthDto> GetHealthAsync(CancellationToken ct = default);
}
