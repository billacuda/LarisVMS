using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Records the two evidence-lock moments nobody clicks: a lock reaching its expiry, and a removed
/// lock's grace period ending. Each is stamped on the row and audited once. Protection itself never
/// depends on this sweep — every query decides it from ExpiresAtUtc/RemovedAtUtc directly — so a
/// late tick only delays the audit entry, never the footage's fate. Same one-minute cadence and shape
/// as RoleAssignmentExpirySweepService.
/// </summary>
public class EvidenceLockSweepService(IServiceScopeFactory scopeFactory, ILogger<EvidenceLockSweepService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await SweepAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IAuditService>(), DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Evidence lock sweep failed — will retry on the next tick.");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>internal for tests.</summary>
    internal static async Task SweepAsync(ApplicationDbContext db, IAuditService audit, DateTime nowUtc, CancellationToken ct)
    {
        var expired = await db.EvidenceLocks.Include(l => l.Camera)
            .Where(l => l.RemovedAtUtc == null && l.ExpiredAtUtc == null && l.ExpiresAtUtc != null && l.ExpiresAtUtc <= nowUtc)
            .ToListAsync(ct);
        foreach (var l in expired)
        {
            l.ExpiredAtUtc = nowUtc;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("EvidenceLock.Expired", null, "System", null,
                $"The lock on {l.Camera.Name} footage {Utc(l.FromUtc)} to {Utc(l.ToUtc)} (placed by {l.CreatedByUserName ?? l.CreatedByUserId}) expired. " +
                "The footage is now subject to normal retention.", ct);
        }

        var graceCutoff = nowUtc - EvidenceLockRules.RemovalGrace;
        var graceEnded = await db.EvidenceLocks.Include(l => l.Camera)
            .Where(l => l.RemovedAtUtc != null && l.GraceEndedAtUtc == null && l.RemovedAtUtc <= graceCutoff)
            .ToListAsync(ct);
        foreach (var l in graceEnded)
        {
            l.GraceEndedAtUtc = nowUtc;
            await db.SaveChangesAsync(ct);
            await audit.LogAsync("EvidenceLock.GraceEnded", null, "System", null,
                $"The 24-hour grace period for the removed lock on {l.Camera.Name} footage {Utc(l.FromUtc)} to {Utc(l.ToUtc)} " +
                $"(removed by {l.RemovedByUserName ?? l.RemovedByUserId}) has ended. The footage is now subject to normal retention.", ct);
        }
    }

    private static string Utc(DateTime value)
        => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
