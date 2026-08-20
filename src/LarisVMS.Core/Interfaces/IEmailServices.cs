using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Interfaces;

/// <summary>
/// The app-facing "send an alert email" surface — resolves EmailSettings, picks the configured
/// provider through IEmailProvider, and sends. Everything that needs to email someone (M15's alert
/// evaluator, the admin test-send button) goes through this rather than talking to a provider
/// directly. Throws InvalidOperationException if email isn't enabled/configured, so callers that
/// genuinely can't proceed without it (a test-send) get a clear error, and callers that should
/// silently no-op when email isn't set up (future alert delivery) check IsConfiguredAsync first.
/// </summary>
public interface IEmailService
{
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);
    Task SendAsync(string to, string subject, string htmlBody, string? textBody = null, CancellationToken ct = default);
}

/// <summary>
/// One provider strategy per EmailProviderType, resolved by EmailProviderFactory — ported from
/// rsolva's IEmailProvider/EmailProviderFactory (src/rsolva.Infrastructure/Email). configJson is an
/// opaque, provider-shaped blob (SmtpEmailConfig for Smtp, etc.) so this interface doesn't grow a new
/// parameter every time a provider with different config fields (OAuth client/tenant ids for Graph
/// and Gmail) gets added.
/// </summary>
public interface IEmailProvider
{
    EmailProviderType ProviderType { get; }
    Task SendAsync(string configJson, string fromAddress, string fromName, string to, string subject,
        string htmlBody, string? textBody = null, CancellationToken ct = default);
}

/// <summary>
/// The "connect this account to an OAuth2 provider" half of a provider that needs per-user consent
/// (Gmail today) — separate from IEmailProvider because it runs once, at connect time, from the admin
/// browser's own redirect rather than per-send. Ported from rsolva's IOAuthConnectProvider
/// (src/rsolva.Core/Interfaces), resolved the same dictionary-by-ProviderType way through
/// OAuthConnectProviderFactory as IEmailProvider is through EmailProviderFactory.
/// </summary>
public interface IOAuthConnectProvider
{
    EmailProviderType ProviderType { get; }
    string BuildAuthorizationUrl(string clientId, string redirectUri, string state);
    Task<string> ExchangeCodeForRefreshTokenAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct = default);
}
