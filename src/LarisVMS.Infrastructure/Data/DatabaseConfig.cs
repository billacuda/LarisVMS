using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LarisVMS.Core.Enums;
using Microsoft.Extensions.Configuration;

namespace LarisVMS.Infrastructure.Data;

/// <summary>
/// The database settings in setup-generated.json: which server type (Database:Provider), the
/// connection string without its password (ConnectionStrings:DefaultConnection), and the password
/// itself DPAPI-protected to this machine (Database:ProtectedPassword). Read the connection string
/// through <see cref="GetConnectionString"/>, never IConfiguration.GetConnectionString — only this
/// puts the password back. Off Windows there is no DPAPI, and the password stays in the connection
/// string, protected only by the file's ACL as before.
/// </summary>
public static class DatabaseConfig
{
    public const string ConnectionStringKey = "ConnectionStrings:DefaultConnection";
    public const string ProviderKey = "Database:Provider";
    public const string ProtectedPasswordKey = "Database:ProtectedPassword";

    private static readonly byte[] Entropy = "LarisVMS.DatabasePassword"u8.ToArray();

    // The DbContext options callback runs once per scope; unprotecting on every request isn't needed.
    private static readonly ConcurrentDictionary<string, string> Unprotected = new(StringComparer.Ordinal);

    public static DatabaseProvider GetProvider(IConfiguration configuration) =>
        Enum.TryParse<DatabaseProvider>(configuration[ProviderKey], ignoreCase: true, out var provider)
            ? provider
            : DatabaseProvider.SqlServer;

    public static DatabaseProviderOps GetOps(IConfiguration configuration) => DatabaseProviderOps.For(GetProvider(configuration));

    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration[ConnectionStringKey]);

    /// <summary>The full connection string, password included; null before setup.</summary>
    public static string? GetConnectionString(IConfiguration configuration)
    {
        var cs = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(cs)) return null;
        var protectedPassword = configuration[ProtectedPasswordKey];
        if (string.IsNullOrWhiteSpace(protectedPassword)) return cs;
        return GetOps(configuration).WithPassword(cs, Unprotected.GetOrAdd(protectedPassword, Unprotect));
    }

    /// <summary>Writes the three keys into setup-generated.json's root and the live configuration.</summary>
    public static void Save(JsonObject root, IConfiguration configuration, DatabaseProvider provider, string connectionString)
    {
        var ops = DatabaseProviderOps.For(provider);
        string? protectedPassword = null;
        if (OperatingSystem.IsWindows())
        {
            connectionString = ops.WithoutPassword(connectionString, out var password);
            if (password is not null) protectedPassword = Protect(password);
        }

        if (root["ConnectionStrings"] is not JsonObject connectionStrings)
            root["ConnectionStrings"] = connectionStrings = new JsonObject();
        connectionStrings["DefaultConnection"] = connectionString;

        if (root["Database"] is not JsonObject database)
            root["Database"] = database = new JsonObject();
        database["Provider"] = provider.ToString();
        if (protectedPassword is null) database.Remove("ProtectedPassword");
        else database["ProtectedPassword"] = protectedPassword;

        configuration[ProviderKey] = provider.ToString();
        configuration[ProtectedPasswordKey] = protectedPassword;
        configuration[ConnectionStringKey] = connectionString;
    }

    /// <summary>Moves a plaintext password (any install configured before passwords were protected)
    /// out of the connection string in setup-generated.json. True when the file changed.</summary>
    public static bool ProtectPlaintextPassword(string setupJsonPath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(setupJsonPath)) return false;

        var root = JsonNode.Parse(File.ReadAllText(setupJsonPath))?.AsObject();
        if (root?["ConnectionStrings"]?["DefaultConnection"]?.GetValue<string>() is not { Length: > 0 } cs) return false;

        var provider = Enum.TryParse<DatabaseProvider>(root["Database"]?["Provider"]?.GetValue<string>(), true, out var p)
            ? p
            : DatabaseProvider.SqlServer;
        DatabaseProviderOps.For(provider).WithoutPassword(cs, out var password);
        if (password is null) return false;

        Save(root, new ConfigurationManager(), provider, cs);
        File.WriteAllText(setupJsonPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    private static string Protect(string password)
    {
#pragma warning disable CA1416 // callers check OperatingSystem.IsWindows()
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.LocalMachine));
#pragma warning restore CA1416
    }

    private static string Unprotect(string protectedPassword)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Database:ProtectedPassword can only be read on Windows.");
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedPassword), Entropy, DataProtectionScope.LocalMachine));
    }
}
