using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Keeps <see cref="NodeTlsTargets.Shared"/> in step with the nodes that have encrypted connections
/// turned on and have reported the certificate they serve (every 30 seconds, about one heartbeat, and
/// right after a node's setting changes). Until a node reports one, it's still reached over plain
/// HTTP, so turning it on never cuts a node off.
/// </summary>
public sealed class NodeTlsService(IServiceScopeFactory scopeFactory, ILogger<NodeTlsService> logger) : BackgroundService
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim ReloadNow = new(0, 1);

    /// <summary>The node page calls this after saving, so a change applies at once.</summary>
    public static void RequestReload()
    {
        if (ReloadNow.CurrentCount == 0) ReloadNow.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReloadAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not load the nodes with encrypted connections; keeping the current list.");
            }
            try { await ReloadNow.WaitAsync(ReloadInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => n.EncryptServerConnection && n.LastIpAddress != null && n.LivePort != null
                        && n.ClientEndpointReportedPort != null && n.ClientEndpointCertSha256 != null)
            .Select(n => new { n.LastIpAddress, n.LivePort, n.ClientEndpointReportedPort, n.ClientEndpointCertSha256 })
            .ToListAsync(ct);
        var before = NodeTlsTargets.Shared.Count;
        NodeTlsTargets.Shared.Replace(nodes.Select(n => new NodeTlsTargets.Target(
            n.LastIpAddress!, n.LivePort!.Value, n.ClientEndpointReportedPort!.Value, n.ClientEndpointCertSha256!)));
        if (NodeTlsTargets.Shared.Count != before)
            logger.LogInformation("{Count} node(s) now reached over encrypted connections.", NodeTlsTargets.Shared.Count);
    }
}
