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
/// Confirm your password before admin pages (<see cref="StepUpPolicy"/>): checked locally, or by
/// Active Directory for an AD account, plus a current code when two-factor sign-in is on. A wrong
/// password or code counts toward lockout. Accounts without a password (Microsoft sign-in) sign in
/// again instead.
/// </summary>
[Authorize]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("per-user")]
public class ConfirmModel(
    UserManager<ApplicationUser> userManager,
    LarisSignInManager signInManager,
    ApplicationDbContext db,
    IActiveDirectoryService directory,
    ActiveDirectoryUserSync adSync,
    UserSessionService sessions,
    IAuditService audit,
    ILogger<ConfirmModel> logger) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public string ReturnUrl { get; private set; } = "/";
    public bool HasPassword { get; private set; }
    public bool NeedsCode { get; private set; }

    public class InputModel
    {
        // Language keys: AddDataAnnotationsLocalization looks them up.
        [DataType(DataType.Password), Display(Name = "common.password"), StringLength(1024)]
        public string Password { get; set; } = string.Empty;
        [Display(Name = "account.twoFactor.code"), StringLength(64)]
        public string? Code { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadAsync(user, returnUrl);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        await LoadAsync(user, returnUrl);
        if (!HasPassword) return Page();
        if (UserSessionService.Current(HttpContext) is not { } sessionId) return await SignInAgainAsync();

        if (string.IsNullOrEmpty(Input.Password))
        {
            ModelState.AddModelError(string.Empty, Loc.T("account.login.passwordRequired"));
            return Page();
        }

        var (ok, unavailable) = await CheckPasswordAsync(user);
        if (unavailable)
        {
            ModelState.AddModelError(string.Empty, Loc.T("account.login.cantReachActiveDirectoryRight"));
            return Page();
        }
        if (ok && NeedsCode)
            ok = await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider,
                TwoFactorSignIn.NormalizeCode(Input.Code));

        if (!ok)
        {
            await userManager.AccessFailedAsync(user);
            await audit.LogAsync("User.ConfirmFailed", user.Id, User.Identity?.Name, Ip, "Wrong password or code", HttpContext.RequestAborted);
            if (await userManager.IsLockedOutAsync(user))
            {
                await signInManager.SignOutAsync();
                return Redirect("/Identity/Account/Login");
            }
            ModelState.AddModelError(string.Empty, Loc.T(NeedsCode ? "account.confirm.wrongPasswordOrCode" : "account.password.wrongCurrent"));
            return Page();
        }

        await userManager.ResetAccessFailedCountAsync(user);
        await sessions.ConfirmAsync(sessionId, DateTime.UtcNow + StepUpPolicy.Window);
        return LocalRedirect(ReturnUrl);
    }

    /// <summary>For an account without a password: a fresh sign-in counts as confirming.</summary>
    public Task<IActionResult> OnPostSignInAgainAsync(string? returnUrl = null)
    {
        ReturnUrl = SafeReturnUrl(returnUrl);
        return SignInAgainAsync();
    }

    private async Task<IActionResult> SignInAgainAsync()
    {
        await signInManager.SignOutAsync();
        return RedirectToPage("./Login", new { returnUrl = ReturnUrl });
    }

    /// <summary>(right, Active Directory couldn't be asked).</summary>
    private async Task<(bool Ok, bool Unavailable)> CheckPasswordAsync(ApplicationUser user)
    {
        if (!await IsActiveDirectoryAsync(user))
            return (await userManager.CheckPasswordAsync(user, Input.Password), false);

        var settings = await db.ActiveDirectorySettings.AsNoTracking().FirstOrDefaultAsync();
        if (settings is not { IsEnabled: true }) return (false, false);
        try
        {
            var groupSids = await adSync.GetLinkedGroupSidsAsync(HttpContext.RequestAborted);
            var entry = await directory.AuthenticateAsync(settings, user.UserName ?? "", Input.Password, groupSids, HttpContext.RequestAborted);
            return (entry.IsActive, false);
        }
        catch (AdInvalidCredentialsException)
        {
            return (false, false);
        }
        catch (ActiveDirectoryException ex)
        {
            logger.LogWarning(ex, "Confirming the password for {UserId} failed: AD unavailable.", user.Id);
            return (false, true);
        }
    }

    private Task<bool> IsActiveDirectoryAsync(ApplicationUser user) =>
        db.UserLogins.AnyAsync(l => l.UserId == user.Id && l.LoginProvider == ActiveDirectoryUserSync.LoginProvider);

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");

    private async Task LoadAsync(ApplicationUser user, string? returnUrl)
    {
        ReturnUrl = SafeReturnUrl(returnUrl);
        HasPassword = await IsActiveDirectoryAsync(user) || await userManager.HasPasswordAsync(user);
        NeedsCode = HasPassword && await userManager.GetTwoFactorEnabledAsync(user);
    }
}
