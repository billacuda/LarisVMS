using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Auth;

namespace LarisVMS.Web.Areas.Identity.Pages.Account;

/// <summary>
/// Local override of the packaged Identity UI's default Login page — needed for one reason: the
/// default renders a "Sign in with {scheme}" button for every *registered* external authentication
/// scheme unconditionally, but Entra should only be offered once an admin has actually configured and
/// enabled it (EntraSsoSettings.IsEnabled), independent of the "EntraID" scheme being registered in
/// the pipeline at all times (Program.cs). Otherwise deliberately smaller than the full default
/// template: no 2FA, no separate Lockout page — this app has neither.
/// </summary>
[AllowAnonymous]
public class LoginModel(SignInManager<ApplicationUser> signInManager, ApplicationDbContext db) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();

    public string? ReturnUrl { get; set; }

    [TempData] public string? ErrorMessage { get; set; }

    public bool ShowEntraButton { get; set; }
    public static string EntraSchemeName => EntraOidcOptionsConfigurator.SchemeName;

    public class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
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
        await LoadEntraButtonStateAsync();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        ReturnUrl = returnUrl ?? Url.Content("~/");
        await LoadEntraButtonStateAsync();
        if (!ModelState.IsValid) return Page();

        var result = await signInManager.PasswordSignInAsync(Input.Email, Input.Password, Input.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded) return LocalRedirect(ReturnUrl);

        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "This account is locked out."
            : "Invalid email or password.");
        return Page();
    }

    private async Task LoadEntraButtonStateAsync()
        => ShowEntraButton = await db.EntraSsoSettings.AsNoTracking()
            .Select(s => s.IsEnabled).FirstOrDefaultAsync();
}
