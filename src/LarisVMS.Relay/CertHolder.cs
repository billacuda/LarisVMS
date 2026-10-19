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
/// <param name="AutoRenewSelfSigned">Regenerate the self-signed certificate on its own when it nears
/// expiry. Off for the web server: nodes pin its self-signed certificate, so a new one would cut them
/// off; an admin regenerates it deliberately (<see cref="CertHolder.RegenerateSelfSigned"/>).</param>
public record CertHolderOptions(string? PfxPath, string? PfxPassword, bool AllowInsecure, string Host,
    string SelfSignedPfxPath, bool AutoRenewSelfSigned = true);

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
    // What self-signed files written before random passwords used; still read, then re-saved.
    private const string LegacySelfSignedPassword = "larisvms-endpoint";

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

        if (options.AutoRenewSelfSigned && IsSelfSigned && _current is { } current && current.NotAfter.ToUniversalTime() - DateTime.UtcNow < RenewWindow)
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

    /// <summary>Replaces the self-signed certificate with a new one (an admin's explicit action).
    /// Returns false, with <see cref="LastError"/>, when it couldn't, or when a real pfx is in use.</summary>
    public bool RegenerateSelfSigned()
    {
        if (!IsSelfSigned) return false;
        try
        {
            _current = CreateAndPersistSelfSigned();
            logger.LogWarning("Self-signed certificate regenerated by an admin. NotAfter {NotAfter:u}.", _current.NotAfter);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not regenerate the self-signed certificate: {ex.Message}";
            logger.LogError(ex, "{Msg}", LastError);
            return false;
        }
    }

    private X509Certificate2 LoadOrCreateSelfSigned()
    {
        if (File.Exists(options.SelfSignedPfxPath))
        {
            try
            {
                var bytes = File.ReadAllBytes(options.SelfSignedPfxPath);
                var password = SelfSignedPasswordFile.Read(options.SelfSignedPfxPath);
                X509Certificate2 existing;
                if (password is not null)
                {
                    existing = X509CertificateLoader.LoadPkcs12(bytes, password);
                }
                else
                {
                    // An older file under the fixed password: re-save it under a random one. Same
                    // certificate, so the thumbprint nodes pinned doesn't change.
                    using var legacy = X509CertificateLoader.LoadPkcs12(bytes, LegacySelfSignedPassword, X509KeyStorageFlags.Exportable);
                    existing = Persist(legacy);
                    logger.LogInformation("Self-signed certificate re-saved under a random password.");
                }
                if (!options.AutoRenewSelfSigned || existing.NotAfter.ToUniversalTime() - DateTime.UtcNow > RenewWindow) return existing;
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
        return Persist(generated);
    }

    /// <summary>Saves the certificate under a new random password (kept beside it, see
    /// <see cref="SelfSignedPasswordFile"/>) and returns it reloaded from those bytes, so the key ends
    /// up in a form Kestrel's TLS stack accepts on Windows.</summary>
    private X509Certificate2 Persist(X509Certificate2 cert)
    {
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pfxBytes = cert.Export(X509ContentType.Pkcs12, password);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(options.SelfSignedPfxPath)!);
            // Password first: a crash in between leaves a new password beside the old file, which
            // reads as unreadable and makes a new certificate rather than silently mismatching.
            SelfSignedPasswordFile.Write(options.SelfSignedPfxPath, password);
            File.WriteAllBytes(options.SelfSignedPfxPath, pfxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            logger.LogWarning(ex, "Could not persist the self-signed certificate — a fresh one will be generated after the next restart.");
        }
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password);
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

/// <summary>
/// The self-signed .pfx's password, in "{pfx}.key" beside it: random per certificate, and on Windows
/// protected with DPAPI (machine scope), so a copy of the two files taken off this machine, in a
/// backup or otherwise, doesn't give away the private key. Machine scope rather than the service
/// account's own, so changing the account a service runs as doesn't lock it out of its certificate
/// (and change the thumbprint nodes pinned). The folder itself is limited to SYSTEM, Administrators,
/// and the service accounts.
/// </summary>
internal static class SelfSignedPasswordFile
{
    private static readonly byte[] Entropy = "LarisVMS.SelfSignedPfx.v1"u8.ToArray();

    public static string PathFor(string pfxPath) => pfxPath + ".key";

    /// <summary>Null when there's no password file (an older pfx) or it can't be read.</summary>
    public static string? Read(string pfxPath)
    {
        var path = PathFor(pfxPath);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        if (OperatingSystem.IsWindows())
        {
            try { bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine); }
            catch (CryptographicException) { return null; }
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public static void Write(string pfxPath, string password)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        var path = PathFor(pfxPath);
        File.WriteAllBytes(path, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
