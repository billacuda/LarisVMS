using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Web.Services;

/// <summary>Rolls finished hours of events into the activity heatmaps' long-term history
/// (ActivityRollupService), and speed-trap measurements into the speed heatmap's
/// (SpeedRollupService), a couple of minutes past every hour, and once shortly after startup — the
/// first run on an upgraded install backfills from every span still in the database.</summary>
public class ActivityRollupBackgroundService(IServiceScopeFactory scopeFactory, ILogger<ActivityRollupBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PastTheHour = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ActivityRollupService>().RollupAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Activity rollup failed — will retry next hour.");
            }
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SpeedRollupService>().RollupAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Speed rollup failed — will retry next hour.");
            }

            var now = DateTime.UtcNow;
            var next = ActivityRollupService.FloorHour(now).AddHours(1) + PastTheHour;
            try { await Task.Delay(next - now, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
