using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <summary>
/// Failover plan phase 2: every ~15 seconds, probes each enabled <see cref="LarisVMS.Core.Entities.MediaProxy"/>'s
/// own <c>GET /health</c> and writes the verdict to <c>MediaProxy.Healthy</c>. That flag is what
/// <see cref="MediaRoutingService"/> reads at ticket time to decide whether a client can be routed
/// through a proxy or must fall back to direct/central. The probe accepts any TLS certificate — this
/// is a reachability check; the self-signed-vs-real decision is made separately in the router.
/// </summary>
public class ProxyHealthMonitor(IServiceScopeFactory scopeFactory, ILogger<ProxyHealthMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        ConnectTimeout = TimeSpan.FromSeconds(4),
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Proxy health tick failed — will retry.");
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var proxyService = scope.ServiceProvider.GetRequiredService<IProxyService>();

        foreach (var p in await proxyService.ListAsync(ct))
        {
            if (ct.IsCancellationRequested) break;

            if (!p.Enabled)
            {
                await proxyService.UpdateHealthAsync(p.Id, healthy: false, "disabled", ct);
                continue;
            }

            var port = p.ReportedPort ?? p.Port;
            if (string.IsNullOrWhiteSpace(p.Host) || port <= 0)
            {
                await proxyService.UpdateHealthAsync(p.Id, healthy: false, "no routable host/port yet", ct);
                continue;
            }

            var (ok, err) = await ProbeAsync(p.Host, port, ct);
            await proxyService.UpdateHealthAsync(p.Id, ok, err, ct);
        }
    }

    private async Task<(bool ok, string? err)> ProbeAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"https://{host}:{port}/health", ct);
            return resp.IsSuccessStatusCode ? (true, null) : (false, $"/health returned {(int)resp.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
