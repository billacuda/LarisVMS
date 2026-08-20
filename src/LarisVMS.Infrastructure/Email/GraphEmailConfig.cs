namespace LarisVMS.Infrastructure.Email;

/// <summary>Same role as SmtpEmailConfig, for the Graph provider — app-only client-credentials auth,
/// so this carries a client secret rather than a refresh token.</summary>
public record GraphEmailConfig(string TenantId, string ClientId, string ClientSecret, string? SharedMailbox);
