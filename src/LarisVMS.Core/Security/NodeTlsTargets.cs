using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace LarisVMS.Core.Security;

/// <summary>
/// Encrypted connections from the server (and media proxies) to a recorder node, opt-in per node
/// (Nodes → the node → Encrypt connections from the server). The server reaches a node's media and
/// control routes at <c>http://{ip}:{livePort}</c> by default. For a node with encryption on, once it
/// reports the certificate its HTTPS endpoint serves, requests go to <c>https://{ip}:{tlsPort}</c>
/// instead, that certificate is the only one accepted (it's usually self-signed, so it's pinned rather
/// than chained), and the media token travels only in the Authorization header, never in the URL.
///
/// Callers keep building the plain address they always have; <see cref="Rewrite"/> (and the handler
/// from <see cref="CreateHandler"/>, which applies it to every request) moves it to the encrypted
/// one. A node that isn't set up for it, or hasn't reported a certificate yet, keeps the plain
/// address, so turning it on never cuts a node off.
/// </summary>
public sealed class NodeTlsTargets
{
    /// <summary>The one instance each process uses.</summary>
    public static NodeTlsTargets Shared { get; } = new();

    /// <summary>The named HttpClient (on the server) whose handler applies this.</summary>
    public const string HttpClientName = "node";

    /// <summary>One node to reach over HTTPS: its address and plain port as callers know them, the
    /// HTTPS port, and the SHA-256 of the certificate it serves.</summary>
    public readonly record struct Target(string Host, int LivePort, int TlsPort, string CertSha256);

    private volatile Dictionary<(string Host, int LivePort), Target> _targets = new();
    private volatile Dictionary<string, HashSet<string>> _pinsByHost = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _targets.Count;

    public void Replace(IEnumerable<Target> targets)
    {
        var map = new Dictionary<(string, int), Target>();
        var pins = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t.Host) || t.TlsPort <= 0 || string.IsNullOrWhiteSpace(t.CertSha256)) continue;
            var target = t with { Host = t.Host.Trim(), CertSha256 = t.CertSha256.Trim().ToUpperInvariant() };
            map[(HostKey(target.Host), target.LivePort)] = target;
            var pinKey = HostKey(target.Host);
            if (!pins.TryGetValue(pinKey, out var set)) pins[pinKey] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(target.CertSha256);
        }
        _pinsByHost = pins;
        _targets = map;
    }

    public Target? Find(string host, int livePort) =>
        _targets.TryGetValue((HostKey(host), livePort), out var t) ? t : null;

    /// <summary>One spelling per address: an IPv6 address with or without brackets (a node's stored
    /// address has them, a TLS connection's target host doesn't), in its canonical form.</summary>
    internal static string HostKey(string host)
    {
        var bare = host.Trim().TrimStart('[').TrimEnd(']');
        return System.Net.IPAddress.TryParse(bare, out var ip) ? ip.ToString() : bare.ToLowerInvariant();
    }

    /// <summary>The address to use for a node request built against the plain port: unchanged for a
    /// node without encryption; otherwise https/wss on its HTTPS port, without <c>token=</c> in the
    /// query (the caller sends the token as a Bearer header).</summary>
    public Uri Rewrite(Uri uri)
    {
        if (uri.Scheme is not ("http" or "ws") || Find(uri.Host, uri.Port) is not { } target) return uri;
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == "ws" ? "wss" : "https",
            Port = target.TlsPort,
            Query = WithoutToken(uri.Query),
        };
        return builder.Uri;
    }

    internal static string WithoutToken(string query)
    {
        var parts = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("token=", StringComparison.OrdinalIgnoreCase) && !p.Equals("token", StringComparison.OrdinalIgnoreCase));
        return string.Join('&', parts);
    }

    /// <summary>Accepts only the pinned certificate of a node reached over HTTPS. For a
    /// SocketsHttpHandler's or ClientWebSocket's RemoteCertificateValidationCallback, whose sender is
    /// the SslStream (its TargetHostName is the node's address).</summary>
    public bool Check(object? sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        Check((sender as SslStream)?.TargetHostName, certificate);

    public bool Check(string? host, X509Certificate? certificate)
    {
        if (string.IsNullOrWhiteSpace(host) || certificate is null) return false;
        return _pinsByHost.TryGetValue(HostKey(host), out var pins)
               && pins.Contains(CameraCertificatePins.Thumbprint(certificate));
    }

    /// <summary>A handler that sends each request to <see cref="Rewrite"/>'s address and checks the
    /// node's certificate there.</summary>
    public HttpMessageHandler CreateHandler(HttpMessageHandler? inner = null)
    {
        if (inner is null)
        {
            var sockets = new SocketsHttpHandler();
            sockets.SslOptions.RemoteCertificateValidationCallback = Check;
            inner = sockets;
        }
        return new RewritingHandler(this) { InnerHandler = inner };
    }

    private sealed class RewritingHandler(NodeTlsTargets targets) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { } uri) request.RequestUri = targets.Rewrite(uri);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
