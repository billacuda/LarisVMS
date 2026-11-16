using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Auth;

/// <summary>Names the external sign-in an account without a password here uses, for messages like
/// "This account signs in with {provider}".</summary>
public static class ExternalSignInProvider
{
    /// <summary>"Google" for an account linked only to Google; otherwise "Microsoft".</summary>
    public static async Task<string> NameAsync(ApplicationDbContext db, string userId)
    {
        var providers = await db.UserLogins.Where(l => l.UserId == userId).Select(l => l.LoginProvider).Distinct().ToListAsync();
        return providers.Contains(GoogleOidcOptionsConfigurator.SchemeName) && !providers.Contains(EntraOidcOptionsConfigurator.SchemeName)
            ? "Google"
            : "Microsoft";
    }
}
