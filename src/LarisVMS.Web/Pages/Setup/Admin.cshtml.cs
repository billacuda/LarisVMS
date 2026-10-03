using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Setup;

public class AdminModel(ISetupService setupService, IRoleSeedService roleSeed, ICameraGroupSeedService cameraGroupSeed) : PageModel
{
    [BindProperty] public string Email { get; set; } = string.Empty;
    [BindProperty] public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            // Program.cs seeds the built-in roles and the "All Cameras" group at startup, but only
            // when a database is already configured. On a fresh install the wizard's Database step
            // creates it after startup, so seed here too, before the admin is put in "Super Admin".
            // Both are idempotent.
            await roleSeed.SeedAsync();
            await cameraGroupSeed.SeedAsync();
            await setupService.CompleteSetupAsync(Email, Password);
            return RedirectToPage("Storage");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
