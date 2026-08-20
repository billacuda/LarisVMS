using LarisVMS.Core.Enums;

namespace LarisVMS.Web.Services;

/// <summary>The payload protected into the OAuth "state" parameter by Pages/Admin/Settings/Email's
/// connect handler and read back by Pages/Admin/OAuthCallback — a CSRF/replay guard (state came from
/// this server, for this provider, recently) rather than anything Google/the provider needs to see.
/// No account id: unlike rsolva's multi-account EmailAccount, EmailSettings is a singleton, so there's
/// only ever the one row to update.</summary>
public sealed record EmailOAuthState(EmailProviderType Provider, DateTime IssuedAtUtc);
