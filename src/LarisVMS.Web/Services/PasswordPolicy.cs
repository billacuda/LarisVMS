using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Localization;

namespace LarisVMS.Web.Services;

/// <summary>
/// A user claim that sends its holder to Change password before anything else (set when an admin
/// creates the account or resets its password, cleared by the change). A claim rather than a column,
/// so it needs no migration; it rides in the sign-in cookie like the user's other claims.
/// </summary>
public static class PasswordChangeRequirement
{
    public const string ClaimType = "larisvms:must_change_password";
    public const string PagePath = "/Identity/Account/ChangePassword";

    /// <summary>Global setting (Settings → Security): true starts the "Must change it at next
    /// sign-in" box checked when an admin creates an account or resets a password. Off by default.</summary>
    public const string TemporaryAdminPasswordsKey = "Security.TemporaryAdminPasswords";

    public static bool IsRequired(ClaimsPrincipal user) => user.HasClaim(c => c.Type == ClaimType);

    public static async Task RequireAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        if ((await users.GetClaimsAsync(user)).Any(c => c.Type == ClaimType)) return;
        await users.AddClaimAsync(user, new Claim(ClaimType, "true"));
    }

    public static async Task ClearAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        var claims = (await users.GetClaimsAsync(user)).Where(c => c.Type == ClaimType).ToList();
        if (claims.Count > 0) await users.RemoveClaimsAsync(user, claims);
    }
}

/// <summary>
/// Refuses passwords that are long enough but still easy to guess: well-known ones, one character
/// repeated, a plain run like "123456789012", and ones built from the account's own name. An admin can
/// add more (one per line, e.g. a published breached-password list) in
/// %ProgramData%\LarisVMS\password-blocklist.txt; it's re-read when it changes.
/// </summary>
public sealed class CommonPasswordValidator : IPasswordValidator<ApplicationUser>
{
    public static string BlocklistPath { get; } = Path.Combine(LarisVMS.Core.Security.LocalDataAcl.Root, "password-blocklist.txt");

    // Common passwords of 12 or more characters (shorter ones are already refused by length), plus
    // the obvious ones for this product. Compared lower-case.
    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456789012", "1234567890123", "12345678901234", "123123123123", "111111111111",
        "000000000000", "123456123456", "password1234", "password12345", "password123456",
        "passwordpassword", "password123!", "Password1234", "Password123!", "Password@123",
        "qwertyuiop12", "qwertyuiopasdf", "qwerty123456", "qwertyqwerty", "1q2w3e4r5t6y",
        "1qaz2wsx3edc", "zaq12wsxcde3", "iloveyou1234", "letmein12345", "welcome12345",
        "welcome@1234", "administrator", "administrator1", "admin1234567", "adminadmin123",
        "changeme1234", "changemenow!", "trustno1trustno1", "football1234", "baseball1234",
        "superman1234", "starwars1234", "monkey123456", "dragon123456", "sunshine1234",
        "princess1234", "abcdefghijkl", "abcd1234abcd", "abc123abc123", "asdfghjkl123",
        "asdfasdfasdf", "zxcvbnm12345", "computer1234", "internet1234", "security1234",
        "camera123456", "cameras12345", "surveillance", "surveillance1", "recorder1234",
        "larisvms1234", "larisvms12345", "larisvmsadmin", "larisvms!234", "nvradmin1234",
        "springfield1", "summer2024!!", "winter2024!!", "spring2025!!", "summer2025!!",
        "autumn2025!!", "winter2025!!", "spring2026!!", "summer2026!!", "autumn2026!!",
    };

    private static readonly object Gate = new();
    private static HashSet<string> _extra = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _extraStamp = DateTime.MinValue;

    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
    {
        if (string.IsNullOrEmpty(password)) return Task.FromResult(IdentityResult.Success);
        var error = Check(password, user.UserName, user.Email, user.DisplayName, ExtraBlocklist());
        return Task.FromResult(error is null
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = error, Description = Loc.T(error == "common" ? "account.password.common" : "account.password.containsName") }));
    }

    /// <summary>Null when the password is fine; otherwise "common" or "containsName".</summary>
    internal static string? Check(string password, string? userName, string? email, string? displayName, IReadOnlySet<string>? extra = null)
    {
        var lower = password.ToLowerInvariant();
        if (BuiltIn.Contains(lower) || (extra?.Contains(lower) ?? false)) return "common";
        if (lower.Distinct().Count() <= 2) return "common";       // "aaaaaaaaaaaa", "abababababab"
        if (IsRun(lower)) return "common";                        // "123456789012", "abcdefghijkl"

        return NameParts(userName, email, displayName).Any(name => lower.Contains(name, StringComparison.Ordinal))
            ? "containsName" : null;
    }

    private static bool IsRun(string value)
    {
        var steps = 0;
        for (var i = 1; i < value.Length; i++)
            if (Math.Abs(value[i] - value[i - 1]) == 1 || (value[i - 1] == '9' && value[i] == '0')) steps++;
        return steps >= value.Length - 2;
    }

    /// <summary>The account's names, 4 characters or longer: the email's part before "@", the user
    /// name, and each word of the display name.</summary>
    private static IEnumerable<string> NameParts(string? userName, string? email, string? displayName)
    {
        var parts = new List<string?>
        {
            email?.Split('@')[0], userName?.Split('@')[0],
        };
        if (displayName is not null) parts.AddRange(displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return parts.Where(p => p is { Length: >= 4 }).Select(p => p!.ToLowerInvariant()).Distinct();
    }

    private static HashSet<string> ExtraBlocklist()
    {
        try
        {
            var stamp = File.Exists(BlocklistPath) ? File.GetLastWriteTimeUtc(BlocklistPath) : DateTime.MinValue;
            lock (Gate)
            {
                if (stamp == _extraStamp) return _extra;
                _extra = stamp == DateTime.MinValue
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : File.ReadLines(BlocklistPath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                _extraStamp = stamp;
                return _extra;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (Gate) return _extra; // keep the last good copy
        }
    }
}
