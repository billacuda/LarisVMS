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
    [BindProperty] public bool TrustServerCertificate { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet() { /* The form starts with its defaults. */ }

    public async Task<IActionResult> OnPostAsync()
    {
        try
        {
            // Once an admin exists, the site's database isn't changed from here.
            if (await setupService.IsAdminCreatedAsync())
            {
                ErrorMessage = "Setup has already created an administrator, so the database can't be changed here.";
                return Page();
            }
            // An existing, already set-up LarisVMS database: its admin, branding, storage and node
            // settings are already in place, so the remaining steps would only overwrite them.
            if (await setupService.SetupDatabaseAsync(ServerName, DatabaseName, Username, Password, trustServerCertificate: TrustServerCertificate))
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
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = $"{ex.Message} If this SQL Server uses a self-signed certificate, tick \"Trust this server's certificate\".";
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
