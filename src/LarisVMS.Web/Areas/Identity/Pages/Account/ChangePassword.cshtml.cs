using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.ActiveDirectory;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>
/// Change your own password: the current one is required, and the new one goes through the same
/// rules as an admin-set one (length and <see cref="CommonPasswordValidator"/>). Everyone's other
/// sessions end at once; this one carries on. Active Directory accounts change
/// theirs in AD, and Microsoft-only accounts have none here.
/// </summary>
[Authorize]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("per-user")]
public class ChangePasswordModel(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    ApplicationDbContext db,
    UserSessionService sessions,
    LarisVMS.Core.Interfaces.IAuditService audit) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    /// <summary>Why this account can't change its password here; null when it can.</summary>
    public string? NotHere { get; private set; }

    public bool Required { get; private set; }
    public bool Changed { get; private set; }

    public class InputModel
    {
        // Language keys: AddDataAnnotationsLocalization looks them up.
        [Required(ErrorMessage = "account.login.passwordRequired"), DataType(DataType.Password), Display(Name = "account.password.current")]
        public string CurrentPassword { get; set; } = string.Empty;
        [Required(ErrorMessage = "account.login.passwordRequired"), DataType(DataType.Password), Display(Name = "account.password.new")]
        public string NewPassword { get; set; } = string.Empty;
        [Required(ErrorMessage = "account.login.passwordRequired"), DataType(DataType.Password), Display(Name = "account.password.confirm")]
        [Compare(nameof(NewPassword), ErrorMessage = "account.password.mismatch")]
        public string ConfirmPassword { get; set; } = string.Empty;
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
        if (NotHere is not null || !ModelState.IsValid) return Page();

        // A wrong current password counts toward lockout like a failed sign-in, so this page can't be
        // used to guess it from a session someone left open.
        if (!await userManager.CheckPasswordAsync(user, Input.CurrentPassword))
        {
            await userManager.AccessFailedAsync(user);
            await audit.LogAsync("User.PasswordChangeFailed", user.Id, User.Identity?.Name, Ip, "Wrong current password", HttpContext.RequestAborted);
            if (await userManager.IsLockedOutAsync(user))
            {
                await signInManager.SignOutAsync();
                return Redirect("/Identity/Account/Login");
            }
            ModelState.AddModelError(string.Empty, Loc.T("account.password.wrongCurrent"));
            return Page();
        }
        if (Input.NewPassword == Input.CurrentPassword)
        {
            ModelState.AddModelError(string.Empty, Loc.T("account.password.sameAsCurrent"));
            return Page();
        }

        var result = await userManager.ChangePasswordAsync(user, Input.CurrentPassword, Input.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        await userManager.ResetAccessFailedCountAsync(user);
        await PasswordChangeRequirement.ClearAsync(userManager, user);
        // The change rotated the security stamp, which ends every other session; this one gets a new
        // cookie (without the must-change claim).
        await signInManager.RefreshSignInAsync(user);
        await sessions.EndAllAsync(user.Id, keep: UserSessionService.Current(HttpContext));
        await audit.LogAsync("User.PasswordChanged", user.Id, User.Identity?.Name, Ip, user.Email, HttpContext.RequestAborted);

        if (Required) return LocalRedirect(ReturnUrl!);
        Changed = true;
        Required = false;
        Input = new();
        ModelState.Clear();
        return Page();
    }

    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(ApplicationUser user, string? returnUrl)
    {
        ReturnUrl = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : Url.Content("~/");
        Required = PasswordChangeRequirement.IsRequired(User);
        if (await db.UserLogins.AnyAsync(l => l.UserId == user.Id && l.LoginProvider == ActiveDirectoryUserSync.LoginProvider))
            NotHere = Loc.T("account.password.activeDirectory");
        else if (!await userManager.HasPasswordAsync(user))
            NotHere = Loc.T("account.password.noLocalPassword");
    }
}
