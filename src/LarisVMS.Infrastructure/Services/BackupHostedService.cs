using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// M11: fires the daily scheduled database backup when Backup.Enabled is on and the configured
/// Backup.Time (server local time) has passed without a scheduled run yet today. SQL Express has no
/// SQL Agent, so the app is the scheduler. Ported from rsolva's BackupHostedService near-verbatim —
/// same delay/poll shape ExportJobDispatcher's own doc comment already established as this codebase's
/// precedent for "a periodic background task doing I/O and reporting outcomes," here reading settings
/// through ISettingsResolver (Global scope) rather than rsolva's flat db.Settings table.
/// </summary>
public class BackupHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<BackupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();

                var enabled = await settings.GetAsync("Backup.Enabled", false, ct: stoppingToken);
                if (enabled)
                {
                    var timeSetting = await settings.GetRawAsync("Backup.Time", ct: stoppingToken);
                    var time = TimeOnly.TryParse(timeSetting, out var t) ? t : new TimeOnly(3, 0);

                    var nowLocal = DateTime.Now;
                    var scheduledTodayLocal = nowLocal.Date + time.ToTimeSpan();
                    if (nowLocal >= scheduledTodayLocal)
                    {
                        var scheduledTodayUtc = scheduledTodayLocal.ToUniversalTime();
                        var alreadyRan = await db.BackupHistoryEntries
                            .AnyAsync(b => b.TriggeredBy == "scheduled" && b.StartedAt >= scheduledTodayUtc, stoppingToken);
                        if (!alreadyRan)
                        {
                            var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
                            await backupService.RunBackupAsync("scheduled", stoppingToken);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "BackupHostedService encountered an error.");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
