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
/// Local override of the packaged Identity UI's default ExternalLogin page. The default's callback
/// falls back to a "confirm your email to finish creating an account" registration flow the first time
/// any external identity signs in — this app disabled self-registration app-wide
/// (RegistrationDisabledMiddleware, M14) specifically because a self-created account gets no role and
/// there's no invite/approval step, so that fallback would reopen exactly the hole that closed. Instead:
/// first-ever sign-in for an external identity only succeeds for an account an admin linked to that
/// Entra object ID (Admin &gt; Settings &gt; Users, see EntraLinks); the pending link is replaced by
/// the real one (UserManager.AddLoginAsync, AspNetUserLogins). Anything else is rejected, not
/// registered — and never matched by email.
/// </summary>
[AllowAnonymous]
public class ExternalLoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager,
    LarisVMS.Infrastructure.Data.ApplicationDbContext db, LarisVMS.Core.Interfaces.IAuditService audit) : PageModel
{
    [TempData] public string? ErrorMessage { get; set; }

    private static string SsoOffMessage => Loc.T("account.externalLogin.microsoftSignInIsTurned");

    // Turning SSO off has to stop sign-ins, not just hide the button: an Entra account already
    // linked could otherwise still sign in while the tenant settings are kept.
    private Task<bool> IsSsoEnabledAsync() =>
        db.EntraSsoSettings.AsNoTracking().Select(s => s.IsEnabled).FirstOrDefaultAsync();

    /// <summary>Starts the challenge — posted from Login.cshtml's "Sign in with Microsoft" button.</summary>
    public async Task<IActionResult> OnPostAsync(string provider, string? returnUrl = null)
    {
        returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
        if (!await IsSsoEnabledAsync())
        {
            ErrorMessage = SsoOffMessage;
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }
        var redirectUrl = Url.Page("./ExternalLogin", pageHandler: "Callback", values: new { returnUrl });
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    /// <summary>Records a refused Microsoft sign-in in the audit log, like a refused password one.
    /// Everything here can come from outside (the query string, the token's claims), so it's trimmed
    /// and stripped of control characters.</summary>
    private Task AuditFailureAsync(string details, ClaimsPrincipal? external = null)
    {
        static string Safe(string? value) =>
            new((value ?? "").Trim().Take(100).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        var name = external?.FindFirst(ClaimTypes.Email)?.Value ?? external?.FindFirst("preferred_username")?.Value
            ?? external?.Identity?.Name;
        return audit.LogAsync("Login.Failure", null, name is null ? null : Safe(name),
            HttpContext.Connection.RemoteIpAddress?.ToString(), "Microsoft sign-in: " + Safe(details), HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null, string? remoteError = null)
    {
        // Only a page of this site: anything else (or a malformed address) goes to the start page.
        returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");

        if (!await IsSsoEnabledAsync())
        {
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            await AuditFailureAsync("turned off");
            ErrorMessage = SsoOffMessage;
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        if (remoteError is not null)
        {
            await AuditFailureAsync(remoteError);
            ErrorMessage = Loc.T("account.externalLogin.errorFromExternalSignIn", new { remoteError });
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            await AuditFailureAsync("no sign-in information returned");
            ErrorMessage = Loc.T("account.externalLogin.errorLoadingExternalSignIn");
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
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
            await AuditFailureAsync("account locked out", info.Principal);
            ErrorMessage = Loc.T("account.externalLogin.thisAccountIsLockedOut");
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        // Not yet linked: only an account an admin linked to this Entra object ID — never by email,
        // never a new account.
        var objectId = EntraLinks.ObjectIdOf(info.Principal);
        var user = objectId is { } oid
            ? await userManager.FindByLoginAsync(info.LoginProvider, EntraLinks.PendingKey(oid))
            : null;
        if (user is null)
        {
            await AuditFailureAsync("not linked to an account", info.Principal);
            ErrorMessage = EntraLinks.NotLinkedMessage;
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }
        if (await userManager.IsLockedOutAsync(user))
        {
            await AuditFailureAsync("account locked out", info.Principal);
            ErrorMessage = Loc.T("account.externalLogin.thisAccountIsLockedOut");
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        await userManager.RemoveLoginAsync(user, info.LoginProvider, EntraLinks.PendingKey(objectId!.Value));
        var addLoginResult = await userManager.AddLoginAsync(user, info);
        if (!addLoginResult.Succeeded)
        {
            await AuditFailureAsync("couldn't link the account", info.Principal);
            ErrorMessage = Loc.T("account.externalLogin.couldNotLinkThisMicrosoft");
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }
}
