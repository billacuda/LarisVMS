using System.Text;

namespace LarisVMS.Web.Services;

/// <summary>
/// Two-factor sign-in with an authenticator app (TOTP, RFC 6238, through Identity's own
/// authenticator token provider). Accounts that sign in with a password, local or Active Directory,
/// can turn it on; Microsoft sign-ins are left to Entra's own checks.
/// </summary>
public static class TwoFactorSignIn
{
    public const string PagePath = "/Identity/Account/TwoFactor";

    /// <summary>Settings → Security: everyone who signs in with a password must set it up.</summary>
    public const string RequireSettingKey = "Security.RequireTwoFactor";

    public const int RecoveryCodeCount = 10;

    /// <summary>The link an authenticator app reads from the QR code (the Key Uri Format most apps
    /// follow): label "issuer:account", the issuer again as a parameter, six digits every 30 seconds.</summary>
    public static string AuthenticatorUri(string issuer, string account, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
        + $"?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

    /// <summary>The key in groups of four, lower case, for typing in by hand.</summary>
    public static string FormatKey(string key)
    {
        var result = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            if (i > 0) result.Append(' ');
            result.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }
        return result.ToString().ToLowerInvariant();
    }

    /// <summary>A code as typed: spaces and dashes (apps show "123 456") removed.</summary>
    public static string NormalizeCode(string? code) =>
        new((code ?? "").Where(c => c is not (' ' or '-' or '\t')).ToArray());
}
