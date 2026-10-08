using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Relay;

/// <summary>What <see cref="CertHolder"/> needs — kept as plain values so LarisVMS.Node and
/// LarisVMS.Proxy can each fill it from their own config shape. <paramref name="SelfSignedPfxPath"/>
/// is where the auto-generated fallback is persisted (distinct per host so a node and a proxy on the
/// same machine don't collide).</summary>
public record CertHolderOptions(string? PfxPath, string? PfxPassword, bool AllowInsecure, string Host,
    string SelfSignedPfxPath);

/// <summary>
/// Failover plan phase 1/2: holds the certificate an HTTPS listener serves, swappable at runtime.
/// Kestrel's <c>ServerCertificateSelector</c> reads <see cref="Current"/> on every handshake, so
/// replacing the reference is picked up by new connections with no restart.
///
/// Resolution order: a real <c>.pfx</c> when one is configured and loadable; else, when insecure mode
/// is on, a stable 10-year self-signed certificate persisted to <see cref="CertHolderOptions.SelfSignedPfxPath"/>
/// so it survives restarts (a fresh cert every boot would force viewers to re-accept it constantly);
/// else nothing, and the listener does not start.
/// </summary>
public sealed class CertHolder(CertHolderOptions options, ILogger logger)
{
    private readonly CertHolderOptions options = options with { PfxPath = CleanPath(options.PfxPath) };

    private volatile X509Certificate2? _current;
    private DateTime _loadedPfxWriteUtc;

    public static readonly TimeSpan SelfSignedLifetime = TimeSpan.FromDays(3650);
    public static readonly TimeSpan RenewWindow = TimeSpan.FromDays(30);
    private const string SelfSignedPassword = "larisvms-endpoint"; // a local file next to the DPAPI store — not a real secret

    public X509Certificate2? Current => _current;
    public bool IsSelfSigned { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Loads the best available certificate into <see cref="Current"/>. Returns true if one
    /// is now available; false leaves <see cref="LastError"/> describing why not.</summary>
    public bool Load()
    {
        if (TryLoadRealPfx()) return true;

        if (options.AllowInsecure)
        {
            try
            {
                _current = LoadOrCreateSelfSigned();
                IsSelfSigned = true;
                if (string.IsNullOrWhiteSpace(options.PfxPath)) LastError = null;
                logger.LogWarning(
                    "HTTPS endpoint is running on a SELF-SIGNED certificate (insecure mode is on) — viewers must trust it once. NotAfter {NotAfter:u}.",
                    _current!.NotAfter);
                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                LastError = $"Could not generate a self-signed certificate: {ex.Message}";
                logger.LogError(ex, "{Msg}", LastError);
                return false;
            }
        }

        LastError ??= "HTTPS endpoint is enabled but no certificate is available (no valid pfx, and insecure mode is off).";
        logger.LogWarning("{Msg}", LastError);
        return false;
    }

    private bool TryLoadRealPfx()
    {
        if (string.IsNullOrWhiteSpace(options.PfxPath)) return false;
        try
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(options.PfxPath!, options.PfxPassword ?? string.Empty);
            _current = cert;
            IsSelfSigned = false;
            LastError = null;
            _loadedPfxWriteUtc = SafeLastWriteUtc(options.PfxPath!);
            logger.LogInformation("HTTPS endpoint certificate loaded from {Path} — subject {Subject}, NotAfter {NotAfter:u}.",
                options.PfxPath, cert.Subject, cert.NotAfter);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not load pfx '{options.PfxPath}': {ex.Message}";
            logger.LogError(ex, "{Msg}", LastError);
            return false;
        }
    }

    /// <summary>Periodic check (see <see cref="CertWatcherService"/>): reload a real pfx whose file
    /// changed on disk, or regenerate the self-signed cert within <see cref="RenewWindow"/> of expiry.
    /// Returns true if <see cref="Current"/> was swapped.</summary>
    public bool CheckForRenewal()
    {
        if (!string.IsNullOrWhiteSpace(options.PfxPath) && !IsSelfSigned)
        {
            var writeUtc = SafeLastWriteUtc(options.PfxPath!);
            if (writeUtc != default && writeUtc != _loadedPfxWriteUtc)
            {
                logger.LogInformation("pfx {Path} changed on disk — reloading.", options.PfxPath);
                return TryLoadRealPfx();
            }
            return false;
        }

        if (IsSelfSigned && _current is { } current && current.NotAfter.ToUniversalTime() - DateTime.UtcNow < RenewWindow)
        {
            try
            {
                logger.LogWarning("Self-signed certificate is within {Days} days of expiry — regenerating (viewers re-accept once).", RenewWindow.TotalDays);
                _current = CreateAndPersistSelfSigned();
                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                LastError = $"Could not regenerate the self-signed certificate: {ex.Message}";
                logger.LogError(ex, "{Msg}", LastError);
            }
        }

        return false;
    }

    private X509Certificate2 LoadOrCreateSelfSigned()
    {
        if (File.Exists(options.SelfSignedPfxPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(options.SelfSignedPfxPath, SelfSignedPassword);
                if (existing.NotAfter.ToUniversalTime() - DateTime.UtcNow > RenewWindow) return existing;
                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // Corrupt / unreadable — fall through and regenerate.
            }
        }
        return CreateAndPersistSelfSigned();
    }

    private X509Certificate2 CreateAndPersistSelfSigned()
    {
        var host = string.IsNullOrWhiteSpace(options.Host) ? Environment.MachineName : options.Host;
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [Oid.FromOidValue("1.3.6.1.5.5.7.3.1", OidGroup.EnhancedKeyUsage)], critical: false)); // server auth

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        if (!string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        foreach (var ip in LocalIps()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        using var generated = req.CreateSelfSigned(now.AddDays(-1), now.Add(SelfSignedLifetime));
        var pfxBytes = generated.Export(X509ContentType.Pkcs12, SelfSignedPassword);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.SelfSignedPfxPath)!);
            File.WriteAllBytes(options.SelfSignedPfxPath, pfxBytes);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not persist the self-signed certificate — a fresh one will be generated after the next restart.");
        }
        // Reload from the exported bytes so the key ends up in a form Kestrel's TLS stack accepts on
        // Windows.
        return X509CertificateLoader.LoadPkcs12(pfxBytes, SelfSignedPassword);
    }

    /// <summary>Trims whitespace and one pair of surrounding quotes — Explorer's "Copy as path" adds
    /// them, and a quoted path otherwise fails as an invalid file name.</summary>
    public static string? CleanPath(string? path)
    {
        var p = path?.Trim();
        if (p is { Length: >= 2 } && p[0] == '"' && p[^1] == '"') p = p[1..^1].Trim();
        return p;
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }

    private static IEnumerable<IPAddress> LocalIps()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .ToArray();
        }
        catch (SocketException)
        {
            return [];
        }
    }
}
