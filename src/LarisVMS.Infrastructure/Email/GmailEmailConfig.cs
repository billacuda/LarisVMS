namespace LarisVMS.Infrastructure.Email;

/// <summary>Same role as SmtpEmailConfig/GraphEmailConfig, for the Gmail provider — unlike Graph's
/// app-only auth, this carries a refresh token because Gmail only supports per-user OAuth2 (no
/// client-credentials flow for sending mail as a specific mailbox).</summary>
public record GmailEmailConfig(string ClientId, string ClientSecret, string RefreshToken, string EmailAddress);
