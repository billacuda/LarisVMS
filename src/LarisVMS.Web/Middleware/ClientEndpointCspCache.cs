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

    private volatile string _imageSources = TileSource(LarisVMS.Infrastructure.Services.MapService.DefaultTileUrl);

    /// <summary>The map tile server, the only outside place pages load images from (img-src).</summary>
    public string ImageSources => _imageSources;

    private volatile bool _strictScripts;

    /// <summary>Settings → Security: enforce the nonce-only script policy (otherwise report-only).</summary>
    public bool StrictScripts => _strictScripts;

    /// <summary>"https://{s}.tile.example.org/{z}/{x}/{y}.png" → "https://*.tile.example.org"; empty
    /// when the URL isn't a plain http(s) one.</summary>
    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{[^}]*\}",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
    private static readonly System.Text.RegularExpressions.Regex TileHost = new(@"^(\*\.)?[A-Za-z0-9.-]+$",
        System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));

    internal static string TileSource(string? tileUrl)
    {
        if (string.IsNullOrWhiteSpace(tileUrl)) return "";
        var withoutPlaceholders = Placeholder.Replace(tileUrl, "x");
        if (!Uri.TryCreate(withoutPlaceholders, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return "";
        var hostPart = tileUrl[(tileUrl.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var host = Placeholder.Replace(hostPart.Split(':')[0], "*");
        if (!TileHost.IsMatch(host)) return "";
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    /// <summary>Re-read on the next request instead of waiting out <see cref="Ttl"/> (a settings save).</summary>
    public void Invalidate() => _refreshedUtc = DateTime.MinValue;

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
                .Where(p => p.Enabled && p.Host != "" && (p.ReportedPort != null || p.Port > 0))
                .Select(p => new { p.Host, Port = p.ReportedPort ?? p.Port })
                .ToListAsync(ct);

            // Only well-formed hostnames or addresses go into the header: a stored value with spaces
            // or control characters could widen the policy or break every response.
            // Firebase Cloud Messaging registers a browser with these, only while it's turned on.
            var tileUrl = await db.Settings.AsNoTracking().Where(s => s.Key == LarisVMS.Infrastructure.Services.MapService.TileUrlKey)
                .Select(s => s.Value).FirstOrDefaultAsync(ct);
            _imageSources = TileSource(string.IsNullOrWhiteSpace(tileUrl) ? LarisVMS.Infrastructure.Services.MapService.DefaultTileUrl : tileUrl);

            _strictScripts = await db.Settings.AsNoTracking()
                .AnyAsync(s => s.Key == LarisVMS.Web.Security.CspNonce.StrictSettingKey && (s.Value == "True" || s.Value == "true"), ct);

            var firebaseOn = await db.Settings.AsNoTracking()
                .AnyAsync(s => s.Key == "Notifications.Fcm.Enabled" && (s.Value == "True" || s.Value == "true"), ct);
            string[] firebaseSources = firebaseOn
                ? ["https://fcmregistrations.googleapis.com", "https://firebaseinstallations.googleapis.com"]
                : [];

            _sources = string.Join(' ', firebaseSources.Concat(nodeEndpoints.Concat(proxyEndpoints)
                .Where(e => e.Port is > 0 and <= 65535 && LarisVMS.Core.Security.HostNames.IsValid(e.Host))
                .Select(e => new { Host = System.Net.IPAddress.TryParse(e.Host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{e.Host}]" : e.Host, e.Port })
                .SelectMany(e => new[] { $"wss://{e.Host}:{e.Port}", $"https://{e.Host}:{e.Port}" }))
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
