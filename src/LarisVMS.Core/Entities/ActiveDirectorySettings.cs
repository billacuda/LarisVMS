namespace LarisVMS.Core.Entities;

/// <summary>
/// Singleton row (one deployment, one AD domain) holding Active Directory sign-in and group-sync
/// configuration — same one-row shape as EntraSsoSettings, service-account password encrypted at rest
/// through SecretProtection. Read fresh on every sign-in and every sync tick, so changes take effect
/// without a restart.
///
/// LocalLoginsEnabled is the documented recovery switch: it's a plain SQL bit, so an admin locked out
/// by an AD outage can run <c>UPDATE ActiveDirectorySettings SET LocalLoginsEnabled = 1</c> and sign in
/// with the Super Admin created during setup. The migration seeds this row so that command always has
/// something to update.
/// </summary>
public class ActiveDirectorySettings
{
    public const int DefaultSyncIntervalMinutes = 30;
    public const int MinSyncIntervalMinutes = 5;
    public const int MaxSyncIntervalMinutes = 24 * 60;

    public Guid Id { get; set; }
    public bool IsEnabled { get; set; }

    /// <summary>DNS name of the domain, e.g. "corp.example.com" — also used as the LDAP server name
    /// (DNS resolves it to a domain controller). Never a DC=... distinguished name.</summary>
    public string? Domain { get; set; }

    /// <summary>LDAPS on 636 when true (the default); plain LDAP on 389 when false.</summary>
    public bool UseLdaps { get; set; } = true;

    /// <summary>False = integrated security (the web service's own identity — the computer account
    /// when running as LocalSystem on a domain-joined machine).</summary>
    public bool UseServiceAccount { get; set; }

    /// <summary>DOMAIN\username.</summary>
    public string? ServiceAccountUsername { get; set; }
    public string? ServiceAccountPassword { get; set; }

    public int SyncIntervalMinutes { get; set; } = DefaultSyncIntervalMinutes;

    /// <summary>Local (email) sign-in. Only honored while AD is enabled — see
    /// <see cref="LocalLoginsAllowed"/>.</summary>
    public bool LocalLoginsEnabled { get; set; } = true;

    public DateTime? LastSyncStartedAt { get; set; }
    public DateTime? LastSyncCompletedAt { get; set; }
    /// <summary>Null when the last sync succeeded.</summary>
    public string? LastSyncError { get; set; }
    public string? LastSyncSummary { get; set; }

    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }

    public int Port => UseLdaps ? 636 : 389;

    /// <summary>Local sign-in can only be turned off while AD sign-in is on — turning AD off can never
    /// leave the deployment with no way in.</summary>
    public static bool LocalLoginsAllowed(ActiveDirectorySettings? settings) =>
        settings is null || !settings.IsEnabled || settings.LocalLoginsEnabled;

    public static int ClampSyncInterval(int minutes) =>
        Math.Clamp(minutes, MinSyncIntervalMinutes, MaxSyncIntervalMinutes);
}
