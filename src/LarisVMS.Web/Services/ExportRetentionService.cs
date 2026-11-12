using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Removes an export from the Exports page once its files are gone. A node deletes finished export
/// output 7 days after writing it (StorageManager's ExportRetention), so an older export's Download
/// link could only end in "not found". A job is removed once it's older than that and nothing in it is
/// still queued or running. Same hourly, scope-per-tick shape as the other retention services.
/// </summary>
public class ExportRetentionService(IServiceScopeFactory scopeFactory, ILogger<ExportRetentionService> logger) : BackgroundService
{
    /// <summary>Matches the node's own export retention (LarisVMS.Node StorageManager.ExportRetention).</summary>
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var removed = await SweepAsync(db, DateTime.UtcNow, stoppingToken);
                if (removed > 0) logger.LogInformation("Removed {Count} expired export(s) whose files the nodes have deleted.", removed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "ExportRetentionService sweep failed — will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static async Task<int> SweepAsync(ApplicationDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var cutoff = nowUtc - Retention;
        var expired = await db.ExportJobs
            .Include(j => j.Items)
            .Where(j => j.CreatedUtc < cutoff
                && !j.Items.Any(i => i.Status == ExportItemStatus.Queued || i.Status == ExportItemStatus.Running))
            .ToListAsync(ct);
        if (expired.Count == 0) return 0;

        db.ExportJobs.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }
}
