using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// Singleton row (one deployment, one outbound sender) holding the app's alerting-email
/// configuration — the prerequisite M15 flagged for testing everything else in that milestone.
/// SmtpPassword encrypts at rest through SecretProtection + EncryptedNullableStringConverter, same
/// pattern as Camera.Password and Node.MediaSigningKey. Provider-specific fields for Graph/Gmail get
/// added here (not a separate table) once those providers ship, same shape as Smtp's.
/// </summary>
public class EmailSettings
{
    public Guid Id { get; set; }
    public bool IsEnabled { get; set; }
    public EmailProviderType Provider { get; set; } = EmailProviderType.Smtp;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }

    // Microsoft Graph — app-only client-credentials auth (ClientSecretCredential), so there's no
    // refresh token to store: the client secret itself is the durable credential, same as SMTP's
    // password. GraphSharedMailbox is the mailbox SendMail is issued against; null falls back to
    // FromAddress (see GraphEmailProvider).
    public string? GraphTenantId { get; set; }
    public string? GraphClientId { get; set; }
    public string? GraphClientSecret { get; set; }
    public string? GraphSharedMailbox { get; set; }

    // Gmail — per-user OAuth2 (authorization-code + offline refresh token), unlike Graph's app-only
    // flow. GmailRefreshToken is written only by the OAuthCallback page after a successful consent
    // round-trip, never typed directly into the admin form.
    public string? GmailClientId { get; set; }
    public string? GmailClientSecret { get; set; }
    public string? GmailRefreshToken { get; set; }
    public string? GmailEmailAddress { get; set; }

    public DateTime? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
}
