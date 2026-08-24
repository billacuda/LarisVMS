namespace LarisVMS.Core.Entities;

/// <summary>
/// Singleton row (one deployment, one Entra tenant) holding the "Sign in with Microsoft" configuration
/// — mirrors EmailSettings' own shape (one row, secret encrypted at rest through SecretProtection).
/// Single-tenant, app-only client credentials for the OIDC confidential-client flow, matching the
/// Graph email provider's own single-tenant app-registration shape (M15 pass 2) rather than supporting
/// arbitrary Microsoft accounts.
///
/// Deliberately not read once at startup: EntraOidcOptionsConfigurator reads this row fresh on every
/// sign-in challenge, so enabling/disabling or fixing a typo'd secret takes effect immediately, no
/// app restart needed.
/// </summary>
public class EntraSsoSettings
{
    public Guid Id { get; set; }
    public bool IsEnabled { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
}
