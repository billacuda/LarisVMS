using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Failover plan phase 1: the extra <c>connect-src</c> origins the CSP needs so a browser told to
/// stream straight from a recorder node (direct mode) isn't blocked by the app's own Content Security
/// Policy. One space-separated string like
/// <c>wss://nvr1.example.com:4200 https://nvr1.example.com:4200 …</c>, built from every node that is
/// currently advertising a client HTTPS endpoint and refreshed on a 30-second TTL. Empty (CSP
/// unchanged) whenever no node has one — which is the default state.
///
/// Singleton with its own scope for the DB read, the same shape as the background sweep services.
/// </summary>
public sealed class ClientEndpointCspCache(IServiceScopeFactory scopeFactory)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile string _sources = string.Empty;
    private DateTime _refreshedUtc = DateTime.MinValue;

    /// <summary>Current value — safe to read at any time; may be up to <see cref="Ttl"/> stale.</summary>
    public string Sources => _sources;

    public async Task EnsureFreshAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _refreshedUtc < Ttl) return;
        // Non-blocking: if another request is already refreshing, this one just serves the stale value.
        if (!await _refreshLock.WaitAsync(0, ct)) return;
        try
        {
            if (DateTime.UtcNow - _refreshedUtc < Ttl) return;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var nodeEndpoints = await db.Nodes.AsNoTracking()
                .Where(n => n.ClientEndpointHost != null && n.ClientEndpointReportedPort != null)
                .Select(n => new { Host = n.ClientEndpointHost!, Port = n.ClientEndpointReportedPort!.Value })
                .ToListAsync(ct);

            // Failover plan phase 2: a browser routed through a proxy connects to the proxy's origin.
            var proxyEndpoints = await db.MediaProxies.AsNoTracking()
                .Where(p => p.Host != "" && (p.ReportedPort != null || p.Port > 0))
                .Select(p => new { p.Host, Port = p.ReportedPort ?? p.Port })
                .ToListAsync(ct);

            _sources = string.Join(' ', nodeEndpoints.Concat(proxyEndpoints)
                .Where(e => e.Port > 0)
                .SelectMany(e => new[] { $"wss://{e.Host}:{e.Port}", $"https://{e.Host}:{e.Port}" })
                .Distinct(StringComparer.OrdinalIgnoreCase));
            _refreshedUtc = DateTime.UtcNow;
        }
        catch
        {
            // A transient DB hiccup must not take the whole request pipeline down over a header —
            // keep whatever value we had and try again on the next request past the TTL.
            _refreshedUtc = DateTime.UtcNow - Ttl + TimeSpan.FromSeconds(5);
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
