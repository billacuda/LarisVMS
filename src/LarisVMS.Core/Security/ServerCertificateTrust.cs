using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LarisVMS.Core.Security;

/// <summary>How a node or proxy checks the web server's HTTPS certificate (Settings → Nodes →
/// Server certificate check).</summary>
public enum ServerCertificateCheck
{
    /// <summary>A certificate from a trusted CA is always accepted, renewed ones included. An untrusted
    /// (self-signed) one is pinned the first time and accepted while its thumbprint matches, whatever
    /// its expiry date; a different untrusted one is refused until the node is re-registered.</summary>
    TrustOnFirstUse = 0,

    /// <summary>Only a certificate that chains to a trusted CA and matches the server's name.</summary>
    TrustedOnly = 1,

    /// <summary>Any certificate. Not safe: anyone in the network path can pose as the server.</summary>
    Off = 2,
}

/// <summary>
/// The live certificate check for one node or proxy process: every HttpClient it uses to reach the
/// server is built from <see cref="CreateHandler"/>. <paramref name="onChanged"/> is called when the pin
/// or mode changes, so the owner can save them.
/// </summary>
public sealed class ServerCertificateTrust(ServerCertificateCheck mode, string? pinnedThumbprint, Action? onChanged = null)
{
    private readonly object _gate = new();

    public ServerCertificateCheck Mode { get; private set; } = mode;

    /// <summary>SHA-256 thumbprint of the server certificate this machine trusts, hex.</summary>
    public string? PinnedThumbprint { get; private set; } = pinnedThumbprint;

    public static ServerCertificateCheck Parse(string? value) =>
        Enum.TryParse<ServerCertificateCheck>(value, ignoreCase: true, out var parsed) ? parsed : ServerCertificateCheck.TrustOnFirstUse;

    public bool Validate(X509Certificate2? certificate, SslPolicyErrors errors)
    {
        if (Mode == ServerCertificateCheck.Off) return true;
        if (certificate is null) return false;
        var thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);

        bool changed = false, accepted;
        lock (_gate)
        {
            if (errors == SslPolicyErrors.None)
            {
                // Trusted by the CA chain: always fine, and remembered, so a later untrusted
                // certificate posing as this server is refused.
                accepted = true;
                if (Mode == ServerCertificateCheck.TrustOnFirstUse && !Matches(thumbprint)) { PinnedThumbprint = thumbprint; changed = true; }
            }
            else if (Mode == ServerCertificateCheck.TrustedOnly)
            {
                accepted = false;
            }
            else if (PinnedThumbprint is null)
            {
                PinnedThumbprint = thumbprint; // first use
                changed = true;
                accepted = true;
            }
            else
            {
                accepted = Matches(thumbprint); // expiry and name errors don't matter once pinned
            }
        }
        if (changed) onChanged?.Invoke();
        return accepted;
    }

    /// <summary>Takes the mode the server sent. Only called with a response that came over a
    /// connection that already passed the current check, so it can't be downgraded by someone in
    /// between.</summary>
    public void ApplyMode(ServerCertificateCheck newMode)
    {
        if (newMode == Mode) return;
        lock (_gate) Mode = newMode;
        onChanged?.Invoke();
    }

    /// <summary>Pins a thumbprint given at install (--server-cert-thumbprint), so the first connection
    /// is checked as well instead of trusted on first use.</summary>
    public void Pin(string thumbprint)
    {
        lock (_gate) PinnedThumbprint = thumbprint;
        onChanged?.Invoke();
    }

    /// <summary>A SHA-256 thumbprint as typed or pasted ("AB:CD …", any case) → 64 upper-case hex
    /// characters; null when it isn't one.</summary>
    public static string? NormalizeThumbprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = new string(value.Where(c => c is not (' ' or ':' or '-')).ToArray()).ToUpperInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>The thumbprint to pin before a first registration, from --server-cert-thumbprint or
    /// LARISVMS_SERVER_CERT_THUMBPRINT. Null when none was given; an error when one was given but
    /// isn't a SHA-256 thumbprint.</summary>
    public static (string? Thumbprint, string? Error) FromArgs(string? argValue)
    {
        var given = argValue ?? Environment.GetEnvironmentVariable("LARISVMS_SERVER_CERT_THUMBPRINT");
        if (string.IsNullOrWhiteSpace(given)) return (null, null);
        return NormalizeThumbprint(given) is { } thumbprint
            ? (thumbprint, null)
            : (null, "--server-cert-thumbprint must be the server certificate's SHA-256 thumbprint (64 hex characters), as shown on Settings → Nodes.");
    }

    /// <summary>Forgets the pin: the next certificate seen is trusted (--reset-server-pin).</summary>
    public void ResetPin()
    {
        lock (_gate) PinnedThumbprint = null;
        onChanged?.Invoke();
    }

    public HttpClientHandler CreateHandler() => new()
    {
        ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => Validate(certificate, errors),
    };

    private bool Matches(string thumbprint) => string.Equals(PinnedThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase);
}
