using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Drives the setup wizard (Pages/Setup/*): database creation + migration, admin account + RBAC
/// seeding, branding, and the storage/node placeholders that later milestones replace with real
/// StorageTarget/Node entities. Mirrors rsolva's SetupService — see the plan's "Deploy & setup"
/// section for why the shape is reused wholesale (setup-generated.json holds only what's needed
/// before the DB can be read; everything else lives in the Settings table).
/// </summary>
public class SetupService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IConfiguration configuration,
    IHostEnvironment environment) : ISetupService
{
    public async Task<bool> IsSetupCompleteAsync(CancellationToken ct = default)
    {
        if (!await IsDatabaseConfiguredAsync(ct)) return false;
        try
        {
            var flag = await db.Settings.FirstOrDefaultAsync(s => s.Key == "Setup.IsComplete", ct);
            if (flag?.Value == "true") return true;
            return await db.Users.AnyAsync(ct);
        }
        catch { return false; }
    }

    public async Task<bool> IsSetupFinalizedAsync(CancellationToken ct = default)
    {
        if (!await IsDatabaseConfiguredAsync(ct)) return false;
        try
        {
            var flag = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "Setup.IsComplete", ct);
            return flag?.Value == "true";
        }
        catch { return false; }
    }

    public async Task<bool> IsDatabaseConfiguredAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")))
            return false;
        try
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(ct);
            await connection.CloseAsync();
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> IsAdminCreatedAsync(CancellationToken ct = default)
    {
        try { return await db.Users.AnyAsync(ct); }
        catch { return false; }
    }

    public async Task<bool> SetupDatabaseAsync(string serverName, string databaseName, string? username, string? password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("Server name and database name are required.");
        ValidateSqlIdentifier(databaseName, nameof(databaseName));

        var masterCs = BuildConnectionString(serverName, "master", username, password);
        bool existed;

        using (var connection = new SqlConnection(masterCs))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{EscapeSqlLiteral(databaseName)}'";
            existed = ((int?)await command.ExecuteScalarAsync(ct) ?? 0) > 0;

            if (!existed)
            {
                using var createCmd = connection.CreateCommand();
                createCmd.CommandText = $"CREATE DATABASE [{EscapeSqlIdentifier(databaseName)}] COLLATE Latin1_General_100_CI_AS_SC_UTF8";
                await createCmd.ExecuteNonQueryAsync(ct);
            }
        }

        var appCs = BuildConnectionString(serverName, databaseName, username, password);
        var migrateOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(appCs).Options;
        using var migrateCtx = new ApplicationDbContext(migrateOptions);

        // An existing database is used as-is when it's a LarisVMS one (an earlier install, or a
        // reinstall pointed back at its old database) — migrated forward below, never re-seeded — and
        // refused when it holds anything else, before its connection string is saved anywhere.
        var adopted = existed && await IsLarisDatabaseAsync(migrateCtx, databaseName, ct);

        configuration["ConnectionStrings:DefaultConnection"] = appCs;
        MergeSetupJson(cfg => cfg["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = appCs });

        await migrateCtx.Database.MigrateAsync(ct);

        await GrantServiceAccountAsync(serverName, databaseName, username, password, ct);
        if (!adopted) return false;

        // Same test as IsSetupCompleteAsync, but against the database just connected — this request's
        // injected context was built before the connection string existed. A LarisVMS database with no
        // users yet carries on through the wizard; one that's already set up gets the flag (an install
        // interrupted before the Review step never set it) and skips the remaining steps.
        var flag = await migrateCtx.Settings.FirstOrDefaultAsync(s => s.Key == "Setup.IsComplete", ct);
        if (flag?.Value != "true" && !await migrateCtx.Users.AnyAsync(ct)) return false;
        await UpsertSettingAsync(migrateCtx, "Setup.IsComplete", "true", "Set once the setup wizard completes.", ct);
        return true;
    }

    /// <summary>True when the database already holds LarisVMS migrations; false when it's empty;
    /// throws when it has tables of its own but none of ours — migrating into it would mix schemas.</summary>
    private static async Task<bool> IsLarisDatabaseAsync(ApplicationDbContext ctx, string databaseName, CancellationToken ct)
    {
        var connection = ctx.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0";
            if ((int)(await tables.ExecuteScalarAsync(ct) ?? 0) == 0) return false;

            var applied = (await ctx.Database.GetAppliedMigrationsAsync(ct)).ToHashSet(StringComparer.Ordinal);
            if (ctx.Database.GetMigrations().Any(applied.Contains)) return true;

            throw new InvalidOperationException(
                $"The database '{databaseName}' already exists and contains tables that aren't from LarisVMS. Choose a different database name.");
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    public async Task CompleteSetupAsync(string adminEmail, string adminPassword, CancellationToken ct = default)
    {
        if (await IsSetupCompleteAsync(ct)) return;

        // Super Admin and Guest/Viewer (plus the other 6 built-in roles and their RoleProfile rows)
        // are seeded by RoleSeedService at every app startup — including this deployment's very
        // first one, before the setup wizard's own POST can ever reach here — so both already exist
        // by this point. Nothing here creates a role of its own anymore.
        await SeedViewerPermissionsAsync(ct);

        var admin = new ApplicationUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(admin, adminPassword);
        if (!result.Succeeded)
        {
            var errors = string.Join(" ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Unable to create administrator account. {errors}");
        }

        // Any user existing counts as "setup complete" (IsSetupCompleteAsync), so a user left behind
        // without its role would lock the wizard out with an admin that can't administer anything.
        try
        {
            var roleResult = await userManager.AddToRoleAsync(admin, "Super Admin");
            if (!roleResult.Succeeded)
                throw new InvalidOperationException(string.Join(" ", roleResult.Errors.Select(e => e.Description)));
        }
        catch (InvalidOperationException ex)
        {
            await userManager.DeleteAsync(admin);
            throw new InvalidOperationException($"Unable to make the administrator a Super Admin. {ex.Message}", ex);
        }

        if (!await db.AppVersions.AnyAsync(ct))
        {
            db.AppVersions.Add(new AppVersion
            {
                Major = 0, Minor = 1, Patch = 0,
                ReleasedAt = DateTime.UtcNow,
                Notes = "Initial release"
            });
            await db.SaveChangesAsync(ct);
        }
    }

    public Task SaveBrandingAsync(string appName, string primaryColor, CancellationToken ct = default)
    {
        configuration["Branding:AppName"] = appName;
        configuration["Branding:PrimaryColor"] = primaryColor;
        MergeSetupJson(cfg => cfg["Branding"] = new JsonObject
        {
            ["AppName"] = appName,
            ["PrimaryColor"] = primaryColor
        });
        return Task.CompletedTask;
    }

    public async Task<string> GetOrCreateNodeRegistrationKeyAsync(CancellationToken ct = default)
    {
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == "Node.RegistrationKey", ct);
        if (existing is not null) return existing.Value;

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await UpsertSettingAsync("Node.RegistrationKey", key,
            "Bearer key a recorder node presents once at first registration (see install-node.ps1).", ct);
        return key;
    }

    public async Task FinalizeSetupAsync(CancellationToken ct = default)
        => await UpsertSettingAsync("Setup.IsComplete", "true", "Set once the setup wizard completes.", ct);

    public Task<(string? FullPath, string? ContentType)> GetLogoInfoAsync()
    {
        // No logo upload yet in M1 — branding is app name + primary color only.
        return Task.FromResult<(string?, string?)>((null, null));
    }

    /// <summary>Super Admin implicitly has every permission (see PermissionService); Guest/Viewer
    /// (né Viewer) is seeded read-only across the resources that exist so far. The full ~18-resource
    /// matrix named in the plan grows as each milestone's pages land — seeding it now for resources
    /// that don't have pages yet would just be dead rows.</summary>
    /// <summary>Internal rather than private so a test can exercise it directly against a real
    /// RoleManager + in-memory DbContext without needing the full UserManager machinery
    /// CompleteSetupAsync (its only real caller) also requires for admin-account creation.</summary>
    internal async Task SeedViewerPermissionsAsync(CancellationToken ct)
    {
        var role = await roleManager.FindByNameAsync("Guest/Viewer");
        if (role is null) return;

        // "Logs", not "AuditLog": Pages/Logs/AuditLogs is gated by [Authorize("Logs.View")].
        (string Resource, string Action)[] viewerPermissions =
            [("Logs", "View"), ("Settings", "View"), ("Views", "View"), ("Views", "Edit"), ("Playback", "View")];

        foreach (var (resource, action) in viewerPermissions)
        {
            // role.Id — the real IdentityRole GUID — not the literal string "Viewer". Every lookup
            // this app does (PermissionService.HasPermissionAsync/GetGrantedAsync) resolves role
            // *names* to their real Ids and compares Permission.RoleId against those Ids, so a row
            // seeded with the bare name here could never match anything: the Viewer role has never
            // actually held a working permission since this method was first written, for any
            // resource, regardless of the resource-name fix a previous pass made to this same
            // method — that fix was real but sat on top of this deeper bug. Caught by re-deriving
            // this bug from the exact same lookup code the nav-permission work (0.95.0) reads
            // through, not by trusting this method's own prior doc comment. A deployment whose setup
            // already ran has old rows with RoleId = "Viewer" sitting in the database uselessly —
            // see this pass's migration, which repairs them in place rather than leaving them dead.
            var exists = await db.Permissions.AnyAsync(
                p => p.RoleId == role.Id && p.Resource == resource && p.Action == action, ct);
            if (!exists)
            {
                db.Permissions.Add(new Permission
                {
                    Id = Guid.NewGuid(),
                    RoleId = role.Id,
                    Resource = resource,
                    Action = action,
                    IsSystemPermission = true
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private Task UpsertSettingAsync(string key, string value, string description, CancellationToken ct) =>
        UpsertSettingAsync(db, key, value, description, ct);

    private static async Task UpsertSettingAsync(ApplicationDbContext target, string key, string value, string description, CancellationToken ct)
    {
        var existing = await target.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is not null)
        {
            existing.Value = value;
            existing.LastModifiedAt = DateTime.UtcNow;
            existing.LastModifiedBy = "system";
        }
        else
        {
            target.Settings.Add(new Setting
            {
                Id = Guid.NewGuid(),
                Key = key,
                Value = value,
                Description = description,
                IsSystemSetting = true,
                CreatedAt = DateTime.UtcNow,
                LastModifiedAt = DateTime.UtcNow,
                LastModifiedBy = "system"
            });
        }
        await target.SaveChangesAsync(ct);
    }

    private async Task GrantServiceAccountAsync(string serverName, string databaseName, string? username, string? password,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
#pragma warning disable CA1416
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity is null) return;
            var account = identity.Name;
#pragma warning restore CA1416
            if (account is null || !SqlIdentifierPattern.IsMatch(account)) return;

            var masterCs = BuildConnectionString(serverName, "master", username, password);
            using var conn = new SqlConnection(masterCs);
            await conn.OpenAsync(ct);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{EscapeSqlLiteral(account)}')
                        CREATE LOGIN [{EscapeSqlIdentifier(account)}] FROM WINDOWS;";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            conn.ChangeDatabase(databaseName);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{EscapeSqlLiteral(account)}')
                        CREATE USER [{EscapeSqlIdentifier(account)}] FOR LOGIN [{EscapeSqlIdentifier(account)}];
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.database_role_members
                        WHERE member_principal_id = DATABASE_PRINCIPAL_ID(N'{EscapeSqlLiteral(account)}')
                          AND role_principal_id   = DATABASE_PRINCIPAL_ID(N'db_owner'))
                        ALTER ROLE db_owner ADD MEMBER [{EscapeSqlIdentifier(account)}];";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: could not grant service account permissions: {ex.Message}");
        }
    }

    private static string BuildConnectionString(string server, string database, string? username, string? password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Encrypt = true,
            TrustServerCertificate = true
        };

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            b.UserID = username;
            b.Password = password;
        }
        else
        {
            b.IntegratedSecurity = true;
        }

        return b.ConnectionString;
    }

    private void MergeSetupJson(Action<JsonObject> update)
    {
        var path = Path.Combine(environment.ContentRootPath, "setup-generated.json");

        JsonObject root;
        try { root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject() : new JsonObject(); }
        catch { root = new JsonObject(); }

        update(root);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // Database/login names are meant to be plain SQL identifiers, validated up front against an
    // allow-list (Windows account names additionally need '\' for DOMAIN\user) so a crafted name
    // can't break out of the "[...]" identifier or "N'...'" literal contexts below.
    private static readonly System.Text.RegularExpressions.Regex SqlIdentifierPattern =
        new(@"^[A-Za-z0-9_][A-Za-z0-9_ \-\.\$\\]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void ValidateSqlIdentifier(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || !SqlIdentifierPattern.IsMatch(value))
            throw new ArgumentException($"'{fieldName}' contains characters that aren't allowed in a SQL identifier.");
    }

    private static string EscapeSqlLiteral(string input) => input.Replace("'", "''");
    private static string EscapeSqlIdentifier(string input) => input.Replace("]", "]]");
}
