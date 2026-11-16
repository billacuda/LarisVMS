using System.Security.Claims;
using LarisVMS.Core.Localization;

namespace LarisVMS.Web.Auth;

/// <summary>
/// Google sign-in only reaches accounts an admin linked: on Settings → Users the admin enters the
/// person's Google email, stored as a pending login (<see cref="PendingKey"/>). The first sign-in
/// whose ID token carries that email, verified by Google, completes the link, which is then keyed by
/// Google's permanent subject ID, so a later change of email on the Google side can't move it.
/// Unlike Entra, Google vouches for the email (<c>email_verified</c>), so matching on it is safe.
/// </summary>
public static class GoogleLinks
{
    private const string PendingPrefix = "pending-email:";

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string PendingKey(string email) => PendingPrefix + NormalizeEmail(email);

    public static bool IsPending(string providerKey) => providerKey.StartsWith(PendingPrefix, StringComparison.Ordinal);

    /// <summary>The email a pending key was made from, or null for a completed link.</summary>
    public static string? PendingEmailOf(string providerKey) => IsPending(providerKey) ? providerKey[PendingPrefix.Length..] : null;

    /// <summary>A plausible email address: one @ with text on both sides and a dot in the domain.</summary>
    public static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0 && at == value.LastIndexOf('@') && at < value.Length - 1
            && value[(at + 1)..].Contains('.') && !value.Any(char.IsWhiteSpace);
    }

    /// <summary>The sign-in's email, only when Google says it's verified.</summary>
    public static string? VerifiedEmailOf(ClaimsPrincipal principal)
    {
        var verified = principal.FindFirstValue("email_verified");
        if (!string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase)) return null;
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
        return string.IsNullOrWhiteSpace(email) ? null : NormalizeEmail(email);
    }

    /// <summary>The Google Workspace domain of the account (the <c>hd</c> claim); null for a
    /// personal Google account.</summary>
    public static string? HostedDomainOf(ClaimsPrincipal principal) =>
        principal.FindFirstValue("hd") is { Length: > 0 } hd ? hd.Trim().ToLowerInvariant() : null;

    /// <summary>The allowed-domains setting as a list: one per line or comma-separated, lower case.</summary>
    public static IReadOnlyList<string> ParseDomains(string? allowedDomains) =>
        (allowedDomains ?? "").Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant()).Distinct().ToList();

    /// <summary>A bare domain like example.com: no scheme, no @, no spaces, at least one dot.</summary>
    public static bool LooksLikeDomain(string value) =>
        value.Contains('.') && !value.StartsWith('.') && !value.EndsWith('.')
        && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-');

    /// <summary>True when no domains are set, or the account belongs to one of them. A personal
    /// Gmail account has no Workspace domain, so a list refuses it.</summary>
    public static bool IsDomainAllowed(string? allowedDomains, ClaimsPrincipal principal)
    {
        var domains = ParseDomains(allowedDomains);
        if (domains.Count == 0) return true;
        return HostedDomainOf(principal) is { } hd && domains.Contains(hd);
    }

    public static string NotLinkedMessage => Loc.T("server.googleLinks.thisGoogleAccountIsntLinked");
}
