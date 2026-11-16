using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Localization;
using LarisVMS.Web.Auth;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>
/// Local override of the packaged Identity UI's default ExternalLogin page, for both Microsoft (Entra
/// ID) and Google sign-in. The default's callback falls back to a "confirm your email to finish
/// creating an account" registration flow the first time any external identity signs in — this app
/// disabled self-registration app-wide (RegistrationDisabledMiddleware, M14) specifically because a
/// self-created account gets no role and there's no invite/approval step, so that fallback would
/// reopen exactly the hole that closed. Instead: first-ever sign-in for an external identity only
/// succeeds for an account an admin linked (Admin &gt; Settings &gt; Users) — to an Entra object ID
/// (see EntraLinks), or to a Google email that Google has verified (see GoogleLinks). The pending
/// link is replaced by the real one (UserManager.AddLoginAsync, AspNetUserLogins). Anything else is
/// rejected, not registered.
/// </summary>
[AllowAnonymous]
public class ExternalLoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager,
    LarisVMS.Infrastructure.Data.ApplicationDbContext db, LarisVMS.Core.Interfaces.IAuditService audit) : PageModel
{
    private const string Entra = EntraOidcOptionsConfigurator.SchemeName;
    private const string Google = GoogleOidcOptionsConfigurator.SchemeName;

    [TempData] public string? ErrorMessage { get; set; }

    private static bool IsKnown(string? provider) => provider is Entra or Google;

    private static string SsoOffMessage(string provider) => provider == Google
        ? Loc.T("account.externalLogin.googleSignInIsTurned")
        : Loc.T("account.externalLogin.microsoftSignInIsTurned");

    private static string ProviderLabel(string? provider) => provider switch
    {
        Entra => "Microsoft",
        Google => "Google",
        _ => "External",
    };

    // Turning SSO off has to stop sign-ins, not just hide the button: an account already linked
    // could otherwise still sign in while the settings are kept.
    private async Task<bool> IsSsoEnabledAsync(string provider) => provider switch
    {
        Entra => await db.EntraSsoSettings.AsNoTracking().Select(s => s.IsEnabled).FirstOrDefaultAsync(),
        Google => await db.GoogleSsoSettings.AsNoTracking().Select(s => s.IsEnabled).FirstOrDefaultAsync(),
        _ => false,
    };

    /// <summary>Starts the challenge — posted from Login.cshtml's "Sign in with Microsoft" or
    /// "Sign in with Google" button.</summary>
    public async Task<IActionResult> OnPostAsync(string provider, string? returnUrl = null)
    {
        returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
        if (!IsKnown(provider) || !await IsSsoEnabledAsync(provider))
        {
            ErrorMessage = SsoOffMessage(provider);
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }
        var redirectUrl = Url.Page("./ExternalLogin", pageHandler: "Callback", values: new { returnUrl, provider });
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    /// <summary>Records a refused external sign-in in the audit log, like a refused password one.
    /// Everything here can come from outside (the query string, the token's claims), so it's trimmed
    /// and stripped of control characters.</summary>
    private Task AuditFailureAsync(string? provider, string details, ClaimsPrincipal? external = null)
    {
        static string Safe(string? value) =>
            new((value ?? "").Trim().Take(100).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        var name = external?.FindFirst(ClaimTypes.Email)?.Value ?? external?.FindFirst("preferred_username")?.Value
            ?? external?.Identity?.Name;
        return audit.LogAsync("Login.Failure", null, name is null ? null : Safe(name),
            HttpContext.Connection.RemoteIpAddress?.ToString(), $"{ProviderLabel(provider)} sign-in: " + Safe(details), HttpContext.RequestAborted);
    }

    private IActionResult BackToLogin(string returnUrl, string message)
    {
        ErrorMessage = message;
        return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null, string? remoteError = null, string? provider = null)
    {
        // Only a page of this site: anything else (or a malformed address) goes to the start page.
        returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");

        // The provider in the query string only labels an error the provider sent back; once there's
        // a sign-in, the scheme that actually authenticated it is what counts.
        var info = remoteError is null ? await signInManager.GetExternalLoginInfoAsync() : null;
        var scheme = info?.LoginProvider ?? (IsKnown(provider) ? provider : null);

        if (scheme is not null && !await IsSsoEnabledAsync(scheme))
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            await AuditFailureAsync(scheme, "turned off");
            return BackToLogin(returnUrl, SsoOffMessage(scheme));
        }

        if (remoteError is not null)
        {
            await AuditFailureAsync(scheme, remoteError);
            return BackToLogin(returnUrl, Loc.T("account.externalLogin.errorFromExternalSignIn", new { remoteError }));
        }

        if (info is null)
        {
            await AuditFailureAsync(scheme, "no sign-in information returned");
            return BackToLogin(returnUrl, Loc.T("account.externalLogin.errorLoadingExternalSignIn"));
        }

        // Checked on every Google sign-in, linked or not, so narrowing the list takes effect at once.
        if (info.LoginProvider == Google)
        {
            var allowed = await db.GoogleSsoSettings.AsNoTracking().Select(s => s.AllowedDomains).FirstOrDefaultAsync();
            if (!GoogleLinks.IsDomainAllowed(allowed, info.Principal))
            {
                await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
                await AuditFailureAsync(Google, "domain not allowed", info.Principal);
                return BackToLogin(returnUrl, Loc.T("account.externalLogin.googleDomainNotAllowed"));
            }
        }

        // Already linked from a previous sign-in — the common case for a returning user. Goes through
        // the same cookie-issuing path a password sign-in does (ConfigureApplicationCookie's own
        // OnSignedIn audit hook, the per-role session-lifetime chain, and every downstream permission
        // check all apply unmodified — the resulting principal is shaped identically either way).
        var signInResult = await signInManager.ExternalLoginSignInAsync(
            info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
        if (signInResult.Succeeded) return LocalRedirect(returnUrl);
        if (signInResult.IsLockedOut)
        {
            await AuditFailureAsync(info.LoginProvider, "account locked out", info.Principal);
            return BackToLogin(returnUrl, Loc.T("account.externalLogin.thisAccountIsLockedOut"));
        }

        // Not yet linked: only an account an admin linked — by Entra object ID, or by a Google email
        // Google has verified. Never a new account.
        var pendingKey = info.LoginProvider == Google
            ? GoogleLinks.VerifiedEmailOf(info.Principal) is { } email ? GoogleLinks.PendingKey(email) : null
            : EntraLinks.ObjectIdOf(info.Principal) is { } oid ? EntraLinks.PendingKey(oid) : null;
        var user = pendingKey is not null ? await userManager.FindByLoginAsync(info.LoginProvider, pendingKey) : null;
        if (user is null)
        {
            var unverified = info.LoginProvider == Google && pendingKey is null;
            await AuditFailureAsync(info.LoginProvider, unverified ? "email not verified" : "not linked to an account", info.Principal);
            return BackToLogin(returnUrl, info.LoginProvider == Google ? GoogleLinks.NotLinkedMessage : EntraLinks.NotLinkedMessage);
        }
        if (await userManager.IsLockedOutAsync(user))
        {
            await AuditFailureAsync(info.LoginProvider, "account locked out", info.Principal);
            return BackToLogin(returnUrl, Loc.T("account.externalLogin.thisAccountIsLockedOut"));
        }

        await userManager.RemoveLoginAsync(user, info.LoginProvider, pendingKey!);
        var addLoginResult = await userManager.AddLoginAsync(user, info);
        if (!addLoginResult.Succeeded)
        {
            await AuditFailureAsync(info.LoginProvider, "couldn't link the account", info.Principal);
            return BackToLogin(returnUrl, info.LoginProvider == Google
                ? Loc.T("account.externalLogin.couldNotLinkThisGoogle")
                : Loc.T("account.externalLogin.couldNotLinkThisMicrosoft"));
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }
}
