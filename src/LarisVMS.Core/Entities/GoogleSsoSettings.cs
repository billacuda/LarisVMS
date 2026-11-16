namespace LarisVMS.Core.Entities;

/// <summary>
/// Singleton row holding the "Sign in with Google" configuration, the same shape as
/// <see cref="EntraSsoSettings"/> (client secret encrypted at rest). Read fresh on every sign-in
/// challenge by GoogleOidcOptionsConfigurator, so changes apply with no restart.
/// </summary>
public class GoogleSsoSettings
{
    public Guid Id { get; set; }
    public bool IsEnabled { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>Google Workspace domains allowed to sign in, one per line. Blank allows any Google
    /// account, personal Gmail included.</summary>
    public string? AllowedDomains { get; set; }
    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
}
