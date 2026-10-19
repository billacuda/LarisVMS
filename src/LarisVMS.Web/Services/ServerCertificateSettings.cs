using System.Security.Cryptography;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Security;
using LarisVMS.Relay;

namespace LarisVMS.Web.Services;

/// <summary>
/// Settings → Nodes → Server certificate check: how nodes and proxies check this server's HTTPS
/// certificate (sent to them on every heartbeat, see ServerCertificateTrust), and tracking of the
/// certificate itself so a change of a self-signed one — which pinned nodes will refuse — is
/// audited and shown on the Nodes page.
/// </summary>
public static class ServerCertificateSettings
{
    public const string CheckKey = "Nodes.ServerCertificateCheck";
    private const string ThumbprintKey = "Server.CertificateThumbprint";
    public const string ChangedAtKey = "Server.CertificateChangedAt";

    /// <summary>How far ahead of a self-signed certificate's expiry the Nodes page warns.</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(90);

    public static string? Thumbprint(CertHolder certs) => certs.Current?.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>Records the certificate in use; when a self-signed one replaced a different one,
    /// stamps the time and audits it. Returns when it last changed, if ever.</summary>
    public static async Task<DateTime?> TrackAsync(CertHolder certs, ISettingsResolver settings, IAuditService audit, CancellationToken ct = default)
    {
        var current = Thumbprint(certs);
        if (current is null) return null;
        var stored = await settings.GetRawAsync(ThumbprintKey, ct: ct);
        if (!string.Equals(stored, current, StringComparison.OrdinalIgnoreCase))
        {
            await settings.SetGlobalAsync(ThumbprintKey, current, "system", ct);
            if (!string.IsNullOrEmpty(stored) && certs.IsSelfSigned)
            {
                await settings.SetGlobalAsync(ChangedAtKey, DateTime.UtcNow.ToString("o"), "system", ct);
                await audit.LogAsync("Server.CertificateChanged", null, "system", null,
                    $"The server's self-signed certificate changed ({stored[..8]}… → {current[..8]}…). Nodes and proxies that pinned the old one must be re-registered.", ct);
            }
        }
        return DateTime.TryParse(await settings.GetRawAsync(ChangedAtKey, ct: ct), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var changedAt) ? changedAt : null;
    }

    public static string Label(ServerCertificateCheck check) => check switch
    {
        ServerCertificateCheck.TrustedOnly => "Trusted certificate only",
        ServerCertificateCheck.Off => "Don't check (not safe)",
        _ => "Trust on first use",
    };
}
