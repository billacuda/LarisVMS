using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.ActiveDirectory;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Auth;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>
/// Local override of the packaged Identity UI's default Login page. Two reasons: the default renders a
/// "Sign in with {scheme}" button for every *registered* external authentication scheme
/// unconditionally, but Entra should only be offered once an admin has actually configured and
/// enabled it (EntraSsoSettings.IsEnabled); and sign-in names are routed by shape — an email address
/// is a local account, anything without "@" is an Active Directory sAMAccountName. Otherwise
/// deliberately smaller than the full default template: no 2FA, no separate Lockout page — this app
/// has neither.
/// </summary>
[AllowAnonymous]
public class LoginModel(
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    ApplicationDbContext db,
    IActiveDirectoryService directory,
    ActiveDirectoryUserSync adSync,
    ILogger<LoginModel> logger) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    [TempData] public string? ErrorMessage { get; set; }

    public bool ShowEntraButton { get; set; }
    public bool AdEnabled { get; set; }
    public static string EntraSchemeName => EntraOidcOptionsConfigurator.SchemeName;

    public class InputModel
    {
        [Required, Display(Name = "Email or username")] public string Username { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
        [Display(Name = "Remember me?")] public bool RememberMe { get; set; }
    }

    public async Task OnGetAsync(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(ErrorMessage)) ModelState.AddModelError(string.Empty, ErrorMessage);

        ReturnUrl = returnUrl ?? Url.Content("~/");
        // Clears any half-finished external sign-in attempt (e.g. the user navigated back mid-flow)
        // — same cleanup the default page does before showing the form fresh.
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        await LoadPageStateAsync();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
        var adSettings = await LoadPageStateAsync();
        if (!ModelState.IsValid) return Page();

        var error = AdUsername.IsLocal(Input.Username)
            ? await SignInLocalAsync(adSettings)
            : await SignInActiveDirectoryAsync(adSettings);
        if (error is null) return LocalRedirect(ReturnUrl);

        ModelState.AddModelError(string.Empty, error);
        return Page();
    }

    private async Task<string?> SignInLocalAsync(ActiveDirectorySettings? adSettings)
    {
        if (!ActiveDirectorySettings.LocalLoginsAllowed(adSettings))
            return "Local sign-in is turned off. Sign in with your Active Directory username (no @).";

        var result = await signInManager.PasswordSignInAsync(Input.Username.Trim(), Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded) return null;
        return result.IsLockedOut ? "This account is locked out." : "Invalid email or password.";
    }

    private async Task<string?> SignInActiveDirectoryAsync(ActiveDirectorySettings? adSettings)
    {
        if (adSettings is not { IsEnabled: true })
            return "Sign in with your email address.";

        var username = AdUsername.Normalize(Input.Username);

        // An existing account that's locked out (a manual disable, or too many recent failures) is
        // refused before AD is even asked — also what rate-limits password guessing through this page.
        var linked = await FindLinkedUserByNameAsync(username);
        if (linked is not null && await userManager.IsLockedOutAsync(linked))
            return "This account is locked out.";

        AdUserEntry entry;
        try
        {
            var groupSids = await adSync.GetLinkedGroupSidsAsync(HttpContext.RequestAborted);
            entry = await directory.AuthenticateAsync(adSettings, username, Input.Password, groupSids, HttpContext.RequestAborted);
        }
        catch (AdInvalidCredentialsException)
        {
            if (linked is not null) await userManager.AccessFailedAsync(linked);
            return "Invalid username or password.";
        }
        catch (ActiveDirectoryException ex)
        {
            logger.LogWarning(ex, "Active Directory sign-in for {Username} failed: AD unavailable.", username);
            return "Can't reach Active Directory right now. Try again shortly, or contact your administrator.";
        }

        var user = await adSync.SyncSignInAsync(entry, HttpContext.RequestAborted);
        if (user is null)
            return entry.IsActive
                ? "Your Active Directory account isn't in any group that has access to this system."
                : "Invalid username or password.";

        await userManager.ResetAccessFailedCountAsync(user);
        await signInManager.SignInAsync(user, Input.RememberMe);
        return null;
    }

    private async Task<ApplicationUser?> FindLinkedUserByNameAsync(string username)
    {
        var user = await userManager.FindByNameAsync(username);
        if (user is null) return null;
        var isAdUser = await db.UserLogins.AnyAsync(l => l.UserId == user.Id && l.LoginProvider == ActiveDirectoryUserSync.LoginProvider);
        return isAdUser ? user : null;
    }

    private async Task<ActiveDirectorySettings?> LoadPageStateAsync()
    {
        ShowEntraButton = await db.EntraSsoSettings.AsNoTracking().Select(s => s.IsEnabled).FirstOrDefaultAsync();
        var ad = await db.ActiveDirectorySettings.AsNoTracking().FirstOrDefaultAsync();
        AdEnabled = ad?.IsEnabled == true;
        return ad;
    }
}
