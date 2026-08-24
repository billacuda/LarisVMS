using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>
/// Local override of the packaged Identity UI's default ExternalLogin page. The default's callback
/// falls back to a "confirm your email to finish creating an account" registration flow the first time
/// any external identity signs in — this app disabled self-registration app-wide
/// (RegistrationDisabledMiddleware, M14) specifically because a self-created account gets no role and
/// there's no invite/approval step, so that fallback would reopen exactly the hole that closed. Instead:
/// first-ever sign-in for an external identity is matched by email against an existing,
/// admin-provisioned ApplicationUser (Admin &gt; Settings &gt; Users) and linked automatically
/// (UserManager.AddLoginAsync — ASP.NET Core Identity's own AspNetUserLogins table, no new table
/// needed); no match means rejected, not registered.
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

        // Not yet linked. Match by email to an existing, admin-provisioned account — never create one.
        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        var user = string.IsNullOrEmpty(email) ? null : await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            ErrorMessage = DescribeRejection(email);
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        var addLoginResult = await userManager.AddLoginAsync(user, info);
        if (!addLoginResult.Succeeded)
        {
            ErrorMessage = "Could not link this Microsoft account to your existing account. Try again, or contact your administrator.";
            return RedirectToPage("./Login", new { ReturnUrl = returnUrl });
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return LocalRedirect(returnUrl);
    }

    /// <summary>Pure so the exact wording is unit-testable without a real SignInManager/UserManager —
    /// everything upstream of this (the actual DB lookup) is framework calls only an integration test
    /// against a real Identity store could meaningfully exercise.</summary>
    internal static string DescribeRejection(string? email) => string.IsNullOrEmpty(email)
        ? "Your Microsoft account did not report an email address, so it can't be matched to an account here."
        : $"No account exists for \"{email}\". Contact your administrator to be provisioned first.";
}
