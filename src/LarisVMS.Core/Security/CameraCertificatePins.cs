using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LarisVMS.Core.Security;

/// <summary>
/// Optional certificate pinning for cameras reached over HTTPS (Settings → Security). Cameras
/// present self-signed certificates that can't chain to anything, so by default any certificate is
/// accepted. With pinning on, the server remembers each camera address's certificate the first time
/// it connects (trust on first use), and from then on refuses a different one: someone posing as the
/// camera on the network can't read its credentials or feed it false events.
///
/// The server is the one that learns (<see cref="LearnUnknown"/>); it saves new pins and sends them
/// to nodes, which enforce the ones they're given and accept an address the server hasn't seen yet.
/// Keyed by host (name or IP address) without the port.
/// </summary>
public sealed class CameraCertificatePins
{
    /// <summary>The one instance each process uses: the server's and the node's camera clients check it.</summary>
    public static CameraCertificatePins Shared { get; } = new();

    private volatile bool _enabled;
    private volatile ConcurrentDictionary<string, string> _pins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A new address was seen while pinning is on: (host, thumbprint), for the server to save.</summary>
    public event Action<string, string>? Learned;

    /// <summary>A certificate didn't match the pin: (host, pinned, presented).</summary>
    public event Action<string, string, string>? Refused;

    /// <summary>True on the server; false on nodes, which only enforce pins the server sent.</summary>
    public bool LearnUnknown { get; set; } = true;

    public bool Enabled => _enabled;

    public void Replace(bool enabled, IEnumerable<KeyValuePair<string, string>> pins)
    {
        var map = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (host, thumbprint) in pins)
            if (Normalize(host) is { } key && !string.IsNullOrEmpty(thumbprint))
                map[key] = thumbprint.ToUpperInvariant();
        _pins = map;
        _enabled = enabled;
    }

    public static string Thumbprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    /// <summary>For a SocketsHttpHandler's RemoteCertificateValidationCallback, whose sender is the SslStream.</summary>
    public bool Check(object? sender, X509Certificate? certificate) =>
        Check((sender as SslStream)?.TargetHostName, certificate);

    public bool Check(string? host, X509Certificate? certificate)
    {
        if (!_enabled) return true;
        var key = Normalize(host);
        if (key is null || certificate is null) return false;

        var presented = Thumbprint(certificate);
        var pins = _pins;
        if (pins.TryGetValue(key, out var pinned))
        {
            if (string.Equals(pinned, presented, StringComparison.Ordinal)) return true;
            Refused?.Invoke(key, pinned, presented);
            return false;
        }
        if (!LearnUnknown) return true;
        if (pins.TryAdd(key, presented))
        {
            Learned?.Invoke(key, presented);
            return true;
        }
        return Check(host, certificate); // another connection pinned it first
    }

    /// <summary>Lower case, without IPv6 brackets or a trailing dot; null if empty.</summary>
    public static string? Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.StartsWith('[') && h.EndsWith(']')) h = h[1..^1];
        return h.Length == 0 ? null : h;
    }
}
