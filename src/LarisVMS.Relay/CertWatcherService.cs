using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Relay;

/// <summary>
/// Failover plan phase 1/2: every 60 seconds, asks <see cref="CertHolder"/> to pick up a real pfx
/// that changed on disk (a renewal, no restart) or regenerate the self-signed fallback within its
/// renew window. Only registered when the HTTPS endpoint is actually enabled.
/// </summary>
public sealed class CertWatcherService(CertHolder certHolder, ILogger<CertWatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                if (certHolder.CheckForRenewal())
                    logger.LogInformation("Certificate swapped in place — NotAfter now {NotAfter:u}.",
                        certHolder.Current?.NotAfter);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Certificate check failed — will retry next tick.");
            }
        }
    }
}
