using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Setup;

public class DatabaseModel(ISetupService setupService) : PageModel
{
    [BindProperty] public string ServerName { get; set; } = ".";
    [BindProperty] public string DatabaseName { get; set; } = "LarisVMS";
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet() { /* The form starts with its defaults. */ }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            // An existing, already set-up LarisVMS database: its admin, branding, storage and node
            // settings are already in place, so the remaining steps would only overwrite them.
            if (await setupService.SetupDatabaseAsync(ServerName, DatabaseName, Username, Password))
                return Redirect("~/");
            return RedirectToPage("Admin");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 26 or -1 or 2 or 53 && !ServerName.Contains('\\'))
        {
            // "Error locating server/instance" or "server not found" for a name with no instance part.
            // SQL Server Express installs as the named instance SQLEXPRESS, so "." or "localhost" alone
            // looks for a default instance that isn't there.
            ErrorMessage = $"{ex.Message} If you installed SQL Server Express, use {ServerName}\\SQLEXPRESS as the server name.";
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
