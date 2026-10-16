using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Web.Services;

/// <summary>Tamper evidence: once an hour, signs every camera's segment hash chain head that has moved
/// since its last checkpoint (SegmentChainService.CheckpointAllAsync). These independent, timestamped
/// signatures are what an export manifest follows its chain to.</summary>
public class ChainCheckpointService(IServiceScopeFactory scopeFactory, ILogger<ChainCheckpointService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SegmentChainService>().CheckpointAllAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Signing segment chain checkpoints failed — will retry next hour.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
