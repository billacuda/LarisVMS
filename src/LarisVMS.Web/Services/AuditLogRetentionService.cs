using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Sweeps <c>AuditLogs</c> rows past a configured retention window, same shape as
/// <see cref="LogsRetentionService"/> (re-reads the setting every cycle, 0 means keep forever) but
/// operating on database rows via <c>ExecuteDeleteAsync</c> instead of files on disk.
///
/// The audit trail had no sweep at all before this — every row ever written was kept forever, which
/// is why the default here is <b>0 (forever)</b> rather than reusing app-log retention's 14-day
/// default. Application logs are disposable debug output; the audit trail is the compliance record
/// this app exists partly to produce (see AuditLog's own doc comment), and an upgrade must never
/// start silently deleting it. An operator who wants a bounded window opts in explicitly on
/// <c>Admin &rarr; Settings &rarr; Logs</c>.
/// </summary>
public class AuditLogRetentionService(IServiceScopeFactory scopeFactory, ILogger<AuditLogRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    public const string RetentionDaysKey = "AuditLog.RetentionDays";
    public const int DefaultRetentionDays = 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var days = await settings.GetAsync(RetentionDaysKey, DefaultRetentionDays, ct: stoppingToken);
                await SweepAsync(db, days, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AuditLogRetentionService sweep failed — will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>0-means-forever is the same rule LogsRetentionService.ShouldDelete already applies
    /// and already has dedicated tests for (LogsRetentionTests) — not duplicated here. What this
    /// method adds beyond that shared rule (deleting real rows against a real DbContext) is what
    /// AuditLogRetentionServiceTests covers instead.
    ///
    /// RemoveRange + SaveChangesAsync rather than ExecuteDeleteAsync: the latter would be the more
    /// efficient server-side DELETE for SQL Server, but EF Core's InMemory provider — what this
    /// method's own tests run against — doesn't support it at all (throws InvalidOperationException
    /// unconditionally). A sweep every six hours over this app's audit-log volume has no real need
    /// for a set-based DELETE, so portability and testability win here.</summary>
    internal static async Task SweepAsync(ApplicationDbContext db, int retentionDays, CancellationToken ct)
    {
        if (retentionDays <= 0) return; // keep forever

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var expired = await db.AuditLogs.Where(l => l.OccurredAt < cutoff).ToListAsync(ct);
        if (expired.Count == 0) return;

        db.AuditLogs.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
    }
}
