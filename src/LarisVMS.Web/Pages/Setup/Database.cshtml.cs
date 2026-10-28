using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

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
                ErrorMessage = "Setup has already created an administrator, so the database can't be changed here.";
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
            ErrorMessage = $"{ex.Message} If you installed SQL Server Express, use {ServerName}\\SQLEXPRESS as the server name.";
            return Page();
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = $"{ex.Message} If this SQL Server uses a self-signed certificate, tick \"Trust this server's certificate\".";
            return Page();
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.UnableToConnectToHost)
        {
            ErrorMessage = $"{ex.Message} Check the host and port, and that MariaDB accepts network connections (bind-address, firewall).";
            return Page();
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.AccessDenied)
        {
            ErrorMessage = $"{ex.Message} Check the user name and password, and that the user may connect from this machine.";
            return Page();
        }
        catch (MySqlConnector.MySqlException ex) when (ex.ErrorCode == MySqlConnector.MySqlErrorCode.DatabaseAccessDenied)
        {
            ErrorMessage = $"{ex.Message} The user needs permission to create the database, or create it first and grant the user ALL on it.";
            return Page();
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Message.Contains("does not support SSL", StringComparison.OrdinalIgnoreCase))
        {
            // MariaDB before 11.4 has no certificate until one is set up, and the password isn't sent
            // to another machine unencrypted.
            ErrorMessage = $"{ex.Message} This MariaDB server has TLS turned off. Give it a certificate (ssl_cert and ssl_key in my.ini, see Help › Installation), or upgrade to MariaDB 11.4 or later, which sets one up itself.";
            return Page();
        }
        catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException
                                   || ex.InnerException is System.Security.Authentication.AuthenticationException)
        {
            ErrorMessage = $"{ex.Message} If this MariaDB server uses a self-signed certificate, tick \"Trust this server's certificate\".";
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
