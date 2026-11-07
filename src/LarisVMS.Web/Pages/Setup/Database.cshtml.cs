using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;

namespace LarisVMS.Web.Pages.Setup;

public class DatabaseModel(ISetupService setupService) : PageModel
{
    [BindProperty] public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;
    [BindProperty] public string ServerName { get; set; } = ".";
    [BindProperty] public string DatabaseName { get; set; } = "LarisVMS";
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public bool TrustServerCertificate { get; set; }

    // MariaDB's fields are separate so switching back and forth doesn't carry SQL Server's "." over.
    [BindProperty] public string MariaDbHost { get; set; } = "localhost";
    [BindProperty] public int MariaDbPort { get; set; } = 3306;
    [BindProperty] public string MariaDbDatabaseName { get; set; } = "larisvms";
    [BindProperty] public string? MariaDbUsername { get; set; }
    [BindProperty] public string? MariaDbPassword { get; set; }
    [BindProperty] public bool MariaDbTrustServerCertificate { get; set; }

    public string? ErrorMessage { get; set; }

    public void OnGet() { /* The form starts with its defaults. */ }

    public async Task<IActionResult> OnPostAsync()
    {
        var request = Provider == DatabaseProvider.MariaDb
            ? new DatabaseSetupRequest(DatabaseProvider.MariaDb, MariaDbHost.Trim(), MariaDbDatabaseName.Trim(),
                MariaDbUsername?.Trim(), MariaDbPassword, MariaDbTrustServerCertificate, MariaDbPort)
            : new DatabaseSetupRequest(DatabaseProvider.SqlServer, ServerName, DatabaseName, Username, Password, TrustServerCertificate);
        try
        {
            // Once an admin exists, the site's database isn't changed from here.
            if (await setupService.IsAdminCreatedAsync())
            {
                ErrorMessage = Loc.T("setup.database.setupHasAlreadyCreatedAn");
                return Page();
            }
            // An existing, already set-up LarisVMS database: its admin, branding, storage and node
            // settings are already in place, so the remaining steps would only overwrite them.
            if (await setupService.SetupDatabaseAsync(request))
                return Redirect("~/");
            return RedirectToPage("Admin");
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 26 or -1 or 2 or 53 && !ServerName.Contains('\\'))
        {
            // "Error locating server/instance" or "server not found" for a name with no instance part.
            // SQL Server Express installs as the named instance SQLEXPRESS, so "." or "localhost" alone
            // looks for a default instance that isn't there.
            ErrorMessage = Loc.T("setup.database.ifYouInstalledSqlServer", new { message = ex.Message, serverName = ServerName });
            return Page();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = Loc.T("setup.database.ifThisSqlServerUses", new { message = ex.Message });
            return Page();
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.UnableToConnectToHost)
        {
            ErrorMessage = Loc.T("setup.database.checkTheHostAndPort", new { message = ex.Message });
            return Page();
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.AccessDenied)
        {
            ErrorMessage = Loc.T("setup.database.checkTheUserNameAnd", new { message = ex.Message });
            return Page();
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DatabaseAccessDenied)
        {
            ErrorMessage = Loc.T("setup.database.theUserNeedsPermissionTo", new { message = ex.Message });
            return Page();
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Message.Contains("does not support SSL", StringComparison.OrdinalIgnoreCase))
        {
            // MariaDB before 11.4 has no certificate until one is set up, and the password isn't sent
            // to another machine unencrypted.
            ErrorMessage = Loc.T("setup.database.mariaDbNoTls", new { message = ex.Message });
            return Page();
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException
                                   || ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            ErrorMessage = Loc.T("setup.database.ifThisMariadbServerUses", new { message = ex.Message });
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
