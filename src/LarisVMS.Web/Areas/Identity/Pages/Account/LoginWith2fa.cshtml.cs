using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Localization;
using LarisVMS.Web.Auth;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>
/// The second sign-in step for an account with two-factor sign-in on: a code from the authenticator
/// app, or one of the account's recovery codes. Reached only from Login, which leaves the short-lived
/// cookie naming whose code this is. A wrong app code counts toward lockout like a wrong password.
/// </summary>
[AllowAnonymous]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("sign-in")]
public class LoginWith2faModel(
    LarisSignInManager signInManager,
    SignInThrottle throttle,
    LarisVMS.Core.Interfaces.IAuditService audit) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public string ReturnUrl { get; private set; } = "/";
    public bool RememberMe { get; private set; }
    public bool Recovery { get; private set; }

    public class InputModel
    {
        // Language keys: AddDataAnnotationsLocalization looks them up.
        [Required(ErrorMessage = "account.twoFactor.codeRequired"), Display(Name = "account.twoFactor.code")]
        [StringLength(64)]
        public string Code { get; set; } = string.Empty;
        [Display(Name = "account.twoFactor.rememberBrowser")] public bool RememberBrowser { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(bool rememberMe = false, string? returnUrl = null, bool recovery = false)
    {
        Load(rememberMe, returnUrl, recovery);
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
            return RedirectToPage("./Login", new { returnUrl = ReturnUrl });
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(bool rememberMe = false, string? returnUrl = null, bool recovery = false)
    {
        Load(rememberMe, returnUrl, recovery);
        var user = await signInManager.GetTwoFactorAuthenticationUserAsync();
        if (user is null)
        {
            // The step's cookie lasts five minutes.
            TempData["ErrorMessage"] = Loc.T("account.twoFactor.tooSlow");
            return RedirectToPage("./Login", new { returnUrl = ReturnUrl });
        }
        if (!ModelState.IsValid) return Page();

        // Codes are short: the same per-address limit as passwords, keyed by the account.
        var address = HttpContext.Connection.RemoteIpAddress;
        var throttleKey = "2fa:" + user.Id;
        if (throttle.BlockedFor(throttleKey, address) is { } wait)
        {
            ModelState.AddModelError(string.Empty, Loc.T("account.login.tooManyTries", new { minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes)) }));
            return Page();
        }

        var code = TwoFactorSignIn.NormalizeCode(Input.Code);
        var result = Recovery
            ? await signInManager.TwoFactorRecoveryCodeSignInAsync(code)
            : await signInManager.TwoFactorAuthenticatorSignInAsync(code, RememberMe, Input.RememberBrowser);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (result.Succeeded)
        {
            throttle.RecordSuccess(throttleKey, address);
            if (Recovery)
            {
                var left = await signInManager.UserManager.CountRecoveryCodesAsync(user);
                await audit.LogAsync("Login.RecoveryCodeUsed", user.Id, user.UserName, ip, $"{left} recovery codes left", HttpContext.RequestAborted);
            }
            return LocalRedirect(ReturnUrl);
        }
        if (result.IsLockedOut)
        {
            await audit.LogAsync("Login.LockedOut", user.Id, user.UserName, ip, "Two-factor code", HttpContext.RequestAborted);
            ModelState.AddModelError(string.Empty, Loc.T("account.login.thisAccountIsLockedOut"));
            return Page();
        }

        // Identity counts a wrong app code toward lockout itself; a recovery code is a long random
        // one, and this page is rate limited.
        throttle.RecordFailure(throttleKey, address);
        await audit.LogAsync("Login.Failure", user.Id, user.UserName, ip,
            Recovery ? "Wrong recovery code" : "Wrong two-factor code", HttpContext.RequestAborted);
        ModelState.AddModelError(string.Empty, Loc.T(Recovery ? "account.twoFactor.wrongRecoveryCode" : "account.twoFactor.wrongCode"));
        return Page();
    }

    private void Load(bool rememberMe, string? returnUrl, bool recovery)
    {
        ReturnUrl = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
        RememberMe = rememberMe;
        Recovery = recovery;
    }
}
