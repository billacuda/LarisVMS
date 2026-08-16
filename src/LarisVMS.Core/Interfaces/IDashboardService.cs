using LarisVMS.Core.Dtos;

namespace LarisVMS.Core.Interfaces;

public interface IDashboardService
{
    /// <summary>Per-camera health rows plus summary counts — the exact data Pages/Index renders on
    /// first load and GET /api/dashboard returns for its 60s AJAX refresh.</summary>
    Task<DashboardHealthDto> GetHealthAsync(CancellationToken ct = default);
}
