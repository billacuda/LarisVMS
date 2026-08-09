using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;
using NidusVMS.Infrastructure.Data;

namespace NidusVMS.Infrastructure.Services;

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

    public async Task SetupDatabaseAsync(string serverName, string databaseName, string? username, string? password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(databaseName))
            throw new ArgumentException("Server name and database name are required.");
        ValidateSqlIdentifier(databaseName, nameof(databaseName));

        var masterCs = BuildConnectionString(serverName, "master", username, password);

        using (var connection = new SqlConnection(masterCs))
        {
            await connection.OpenAsync(ct);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{EscapeSqlLiteral(databaseName)}'";
            var exists = (int?)await command.ExecuteScalarAsync(ct) ?? 0;

            if (exists == 0)
            {
                using var createCmd = connection.CreateCommand();
                createCmd.CommandText = $"CREATE DATABASE [{EscapeSqlIdentifier(databaseName)}] COLLATE Latin1_General_100_CI_AS_SC_UTF8";
                await createCmd.ExecuteNonQueryAsync(ct);
            }
        }

        var appCs = BuildConnectionString(serverName, databaseName, username, password);
        configuration["ConnectionStrings:DefaultConnection"] = appCs;
        MergeSetupJson(cfg => cfg["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = appCs });

        var migrateOptions = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(appCs).Options;
        using var migrateCtx = new ApplicationDbContext(migrateOptions);
        await migrateCtx.Database.MigrateAsync(ct);

        await GrantServiceAccountAsync(serverName, databaseName, username, password, ct);
    }

    public async Task CompleteSetupAsync(string adminEmail, string adminPassword, CancellationToken ct = default)
    {
        if (await IsSetupCompleteAsync(ct)) return;

        await EnsureRoleAsync("Administrator", ct);
        await EnsureRoleAsync("Viewer", ct);
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

        await userManager.AddToRoleAsync(admin, "Administrator");

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

    public async Task SaveStorageRootAsync(string rootPath, CancellationToken ct = default)
        => await UpsertSettingAsync("Storage.RootPath", rootPath,
            "Default local disk path or UNC share recorder nodes write recordings to.", ct);

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

    private async Task EnsureRoleAsync(string roleName, CancellationToken ct)
    {
        if (!await roleManager.RoleExistsAsync(roleName))
            await roleManager.CreateAsync(new IdentityRole(roleName));
    }

    /// <summary>Administrator implicitly has every permission (see PermissionService); Viewer is
    /// seeded read-only across the resources that exist so far. The full ~18-resource matrix named
    /// in the plan grows as each milestone's pages land — seeding it now for resources that don't
    /// have pages yet would just be dead rows.</summary>
    private async Task SeedViewerPermissionsAsync(CancellationToken ct)
    {
        var role = await roleManager.FindByNameAsync("Viewer");
        if (role is null) return;

        (string Resource, string Action)[] viewerPermissions =
            [("AuditLog", "View"), ("Settings", "View"), ("Views", "View"), ("Views", "Edit"), ("Playback", "View")];

        foreach (var (resource, action) in viewerPermissions)
        {
            var exists = await db.Permissions.AnyAsync(
                p => p.RoleId == "Viewer" && p.Resource == resource && p.Action == action, ct);
            if (!exists)
            {
                db.Permissions.Add(new Permission
                {
                    Id = Guid.NewGuid(),
                    RoleId = "Viewer",
                    Resource = resource,
                    Action = action,
                    IsSystemPermission = true
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task UpsertSettingAsync(string key, string value, string description, CancellationToken ct)
    {
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is not null)
        {
            existing.Value = value;
            existing.LastModifiedAt = DateTime.UtcNow;
            existing.LastModifiedBy = "system";
        }
        else
        {
            db.Settings.Add(new Setting
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
        await db.SaveChangesAsync(ct);
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
