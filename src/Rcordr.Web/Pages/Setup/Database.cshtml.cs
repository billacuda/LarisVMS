using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Rcordr.Core.Interfaces;

namespace Rcordr.Web.Pages.Setup;

public class DatabaseModel(ISetupService setupService) : PageModel
{
    [BindProperty] public string ServerName { get; set; } = ".";
    [BindProperty] public string DatabaseName { get; set; } = "Rcordr";
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            await setupService.SetupDatabaseAsync(ServerName, DatabaseName, Username, Password);
            return RedirectToPage("Admin");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
