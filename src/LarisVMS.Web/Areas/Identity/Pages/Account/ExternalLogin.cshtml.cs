using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
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
public class ExternalLoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager) : PageModel
{
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>Starts the challenge — posted from Login.cshtml's "Sign in with Microsoft" button.</summary>
    public IActionResult OnPost(string provider, string? returnUrl = null)
    {
        var redirectUrl = Url.Page("./ExternalLogin", pageHandler: "Callback", values: new { returnUrl });
        var properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    public async Task<IActionResult> OnGetCallbackAsync(string? returnUrl = null, string? remoteError = null)
    {
        returnUrl ??= Url.Content("~/");

        if (remoteError is not null)
        {
            ErrorMessage = $"Error from external sign-in: {remoteError}";
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        var info = await signInManager.GetExternalLoginInfoAsync();
        if (info is null)
        {
            ErrorMessage = "Error loading external sign-in information.";
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
            ErrorMessage = "This account is locked out.";
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
            ErrorMessage = EntraLinks.NotLinkedMessage;
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }
        if (await userManager.IsLockedOutAsync(user))
        {
            ErrorMessage = "This account is locked out.";
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        await userManager.RemoveLoginAsync(user, info.LoginProvider, EntraLinks.PendingKey(objectId!.Value));
        var addLoginResult = await userManager.AddLoginAsync(user, info);
        if (!addLoginResult.Succeeded)
        {
            ErrorMessage = "Could not link this Microsoft account to your existing account. Try again, or contact your administrator.";
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }
}
