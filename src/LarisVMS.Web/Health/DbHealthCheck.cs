using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Health;

public class DbHealthCheck(ApplicationDbContext db, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Pre-setup, there is no connection string yet — that's an expected state, not a failed
        // health check (deploy.ps1's post-deploy probe would otherwise fail on a fresh install
        // before the wizard has run).
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
            return HealthCheckResult.Healthy("Database not yet configured (setup pending).");

        try
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync(cancellationToken);
            await conn.CloseAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database unreachable.", ex);
        }
    }
}
