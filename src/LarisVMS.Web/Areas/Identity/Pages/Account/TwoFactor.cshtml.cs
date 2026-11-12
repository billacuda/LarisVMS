using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.ActiveDirectory;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Auth;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>
/// Turn two-factor sign-in on or off for your own account. Setting up makes a new key every time (an
/// old QR code someone saw is useless), and it's only on once a code from the app checks out.
/// Changing it afterwards — new recovery codes, or turning it off — takes a current code, and a wrong
/// one counts toward lockout, so a session left open isn't enough. Accounts without a password
/// (Microsoft sign-in only) have nothing to protect here.
/// </summary>
[Authorize]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("per-user")]
public class TwoFactorModel(
    UserManager<ApplicationUser> userManager,
    LarisSignInManager signInManager,
    ApplicationDbContext db,
    ISettingsResolver settings,
    IBrandingService branding,
    UserSessionService sessions,
    IAuditService audit) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public string ReturnUrl { get; private set; } = "/";
    public string? NotHere { get; private set; }
    public bool Enabled { get; private set; }
    public bool Required { get; private set; }
    public int RecoveryCodesLeft { get; private set; }

    /// <summary>Setting up: the QR code (SVG) and the key to type in by hand.</summary>
    public string? QrSvg { get; private set; }
    public string? FormattedKey { get; private set; }

    /// <summary>Shown once, straight after they're made.</summary>
    public IReadOnlyList<string> NewRecoveryCodes { get; private set; } = [];
    public string? Message { get; private set; }

    public class InputModel
    {
        // Language keys: AddDataAnnotationsLocalization looks them up.
        [Required(ErrorMessage = "account.twoFactor.codeRequired"), Display(Name = "account.twoFactor.code")]
        [StringLength(64)]
        public string Code { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null, bool setup = false)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadAsync(user, returnUrl);
        if (setup && !Enabled && NotHere is null) await LoadSetupAsync(user);
        return Page();
    }

    /// <summary>Starts setting up: a new key, then the page with its QR code.</summary>
    public async Task<IActionResult> OnPostStartAsync(string? returnUrl = null)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadAsync(user, returnUrl);
        if (Enabled || NotHere is not null) return Page();

        await userManager.ResetAuthenticatorKeyAsync(user);
        // That changed the security stamp; keep this session.
        await signInManager.RefreshSignInAsync(user);
        return RedirectToPage(new { returnUrl = ReturnUrl, setup = true });
    }

    public async Task<IActionResult> OnPostVerifyAsync(string? returnUrl = null)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadAsync(user, returnUrl);
        if (Enabled || NotHere is not null) return Page();
        if (!ModelState.IsValid)
        {
            await LoadSetupAsync(user);
            return Page();
        }

        var valid = await userManager.VerifyTwoFactorTokenAsync(user,
            userManager.Options.Tokens.AuthenticatorTokenProvider, TwoFactorSignIn.NormalizeCode(Input.Code));
        if (!valid)
        {
            ModelState.AddModelError(string.Empty, Loc.T("account.twoFactor.wrongCode"));
            await LoadSetupAsync(user);
            return Page();
        }

        await userManager.SetTwoFactorEnabledAsync(user, true);
        NewRecoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, TwoFactorSignIn.RecoveryCodeCount))?.ToList() ?? [];
        await signInManager.RefreshSignInAsync(user);
        // Anywhere else signed in with just the password has to sign in again, with a code.
        await sessions.EndAllAsync(user.Id, keep: UserSessionService.Current(HttpContext));
        await audit.LogAsync("User.TwoFactorEnabled", user.Id, User.Identity?.Name, Ip, user.Email ?? user.UserName, HttpContext.RequestAborted);
        return await ShowAsync(user, Loc.T("account.twoFactor.turnedOn"));
    }

    public async Task<IActionResult> OnPostNewCodesAsync(string? returnUrl = null)
    {
        var (user, refusal) = await CheckCurrentCodeAsync(returnUrl, turningOff: false);
        if (refusal is not null) return refusal;

        NewRecoveryCodes = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user!, TwoFactorSignIn.RecoveryCodeCount))?.ToList() ?? [];
        await audit.LogAsync("User.RecoveryCodesRenewed", user!.Id, User.Identity?.Name, Ip, user.Email ?? user.UserName, HttpContext.RequestAborted);
        return await ShowAsync(user, Loc.T("account.twoFactor.codesRenewed"));
    }

    public async Task<IActionResult> OnPostDisableAsync(string? returnUrl = null)
    {
        var (user, refusal) = await CheckCurrentCodeAsync(returnUrl, turningOff: true);
        if (refusal is not null) return refusal;

        await userManager.SetTwoFactorEnabledAsync(user!, false);
        await userManager.ResetAuthenticatorKeyAsync(user!);
        await signInManager.ForgetTwoFactorClientAsync();
        await signInManager.RefreshSignInAsync(user!);
        await audit.LogAsync("User.TwoFactorDisabled", user!.Id, User.Identity?.Name, Ip, user.Email ?? user.UserName, HttpContext.RequestAborted);
        return await ShowAsync(user, Loc.T("account.twoFactor.turnedOff"));
    }

    /// <summary>The account, once the code typed in is a current one from the app (or, for turning
    /// it off after losing the phone, an unused recovery code, which this uses up). Otherwise the
    /// page to show instead.</summary>
    private async Task<(ApplicationUser? User, IActionResult? Refusal)> CheckCurrentCodeAsync(string? returnUrl, bool turningOff)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return (null, Challenge());
        await LoadAsync(user, returnUrl);
        if (!Enabled || NotHere is not null || !ModelState.IsValid) return (user, Page());
        if (turningOff && Required)
        {
            ModelState.AddModelError(string.Empty, Loc.T("account.twoFactor.cantTurnOffRequired"));
            return (user, Page());
        }
        var allowRecoveryCode = turningOff;

        var code = TwoFactorSignIn.NormalizeCode(Input.Code);
        var valid = await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, code)
            || (allowRecoveryCode && (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, code)).Succeeded);
        if (valid) return (user, null);

        await userManager.AccessFailedAsync(user);
        await audit.LogAsync("User.TwoFactorChangeFailed", user.Id, User.Identity?.Name, Ip, "Wrong two-factor code", HttpContext.RequestAborted);
        if (await userManager.IsLockedOutAsync(user))
        {
            await signInManager.SignOutAsync();
            return (user, Redirect("/Identity/Account/Login"));
        }
        ModelState.AddModelError(string.Empty, Loc.T("account.twoFactor.wrongCode"));
        return (user, Page());
    }

    private async Task<IActionResult> ShowAsync(ApplicationUser user, string message)
    {
        await LoadAsync(user, ReturnUrl);
        Message = message;
        Input = new();
        ModelState.Clear();
        return Page();
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(ApplicationUser user, string? returnUrl)
    {
        ReturnUrl = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
        Enabled = await userManager.GetTwoFactorEnabledAsync(user);
        RecoveryCodesLeft = Enabled ? await userManager.CountRecoveryCodesAsync(user) : 0;
        var isAd = await db.UserLogins.AnyAsync(l => l.UserId == user.Id && l.LoginProvider == ActiveDirectoryUserSync.LoginProvider);
        var signsInWithPassword = isAd || await userManager.HasPasswordAsync(user);
        if (!signsInWithPassword) NotHere = Loc.T("account.twoFactor.microsoftOnly");
        Required = signsInWithPassword && await settings.GetAsync(TwoFactorSignIn.RequireSettingKey, false);
    }

    private async Task LoadSetupAsync(ApplicationUser user)
    {
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key)) return; // not started; the page offers Set up
        var issuer = (await branding.GetAsync()).AppName;
        var account = user.Email ?? user.UserName ?? user.Id;
        QrSvg = QrCode.Encode(TwoFactorSignIn.AuthenticatorUri(issuer, account, key)).ToSvg();
        FormattedKey = TwoFactorSignIn.FormatKey(key);
    }
}
