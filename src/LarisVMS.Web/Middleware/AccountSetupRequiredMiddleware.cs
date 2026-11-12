using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.ActiveDirectory;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Someone who has to finish setting up their account before using anything: choose their own
/// password (an admin set it, for a new account or a reset), or set up two-factor sign-in when
/// Settings → Security requires it. Page requests go to that page, everything else is refused. Sign
/// out, the account pages, and static files still work. API keys aren't people and aren't affected.
/// </summary>
public sealed class AccountSetupRequiredMiddleware(RequestDelegate next)
{
    private static readonly string[] Allowed =
    [
        PasswordChangeRequirement.PagePath, TwoFactorSignIn.PagePath, "/Identity/Account/Logout",
        "/Identity/lib", "/Identity/css", "/Identity/js", "/_framework", "/_content", "/api/csp-report",
    ];

    /// <summary>The site's own static files (wwwroot), which the account pages need to show. Only these:
    /// any other path with a file extension (a language file download, a Help page with a dot in its
    /// name, a node build) waits like any page until the account is set up.</summary>
    private static readonly string[] StaticFiles =
    [
        "/css", "/js", "/lib", "/icons", "/locales", "/favicon.ico", "/sw.js", "/manifest.webmanifest",
    ];

    public async Task InvokeAsync(HttpContext context, ISettingsResolver settings, ApplicationDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated != true
            || context.User.HasClaim(c => c.Type == ApiKeyScope.KeyIdClaim)
            || IsAllowed(context.Request.Method, context.Request.Path))
        {
            await next(context);
            return;
        }

        var page = PasswordChangeRequirement.IsRequired(context.User) ? PasswordChangeRequirement.PagePath
            : await NeedsTwoFactorAsync(context, settings, db) ? TwoFactorSignIn.PagePath
            : null;
        if (page is null)
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) && !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && !context.WebSockets.IsWebSocketRequest)
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"{page}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
        }
    }

    /// <summary>Two-factor sign-in is required and this account signs in with a password (local or
    /// Active Directory) but hasn't set it up. Read from the database each time, so turning the
    /// setting on, or an admin turning someone's off, applies on the next request.</summary>
    private static async Task<bool> NeedsTwoFactorAsync(HttpContext context, ISettingsResolver settings, ApplicationDbContext db)
    {
        if (!await settings.GetAsync(TwoFactorSignIn.RequireSettingKey, false, ct: context.RequestAborted)) return false;
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return false;

        var account = await db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.TwoFactorEnabled, HasPassword = u.PasswordHash != null })
            .FirstOrDefaultAsync(context.RequestAborted);
        if (account is null || account.TwoFactorEnabled) return false;
        return account.HasPassword
            || await db.UserLogins.AnyAsync(l => l.UserId == userId && l.LoginProvider == ActiveDirectoryUserSync.LoginProvider, context.RequestAborted);
    }

    /// <summary>The account pages, sign out, and the site's static files (<see cref="StaticFiles"/>).</summary>
    internal static bool IsAllowed(string method, PathString path) =>
        Allowed.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
        || ((HttpMethods.IsGet(method) || HttpMethods.IsHead(method))
            && StaticFiles.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)));
}
