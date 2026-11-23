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
/// itself encrypted to this machine (Database:ProtectedPassword, see MachineSecret: DPAPI on Windows,
/// AES-256-GCM keyed off /etc/machine-id on Linux). Read the connection string through
/// <see cref="GetConnectionString"/>, never IConfiguration.GetConnectionString — only this puts the
/// password back.
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
        connectionString = ops.WithoutPassword(connectionString, out var password);
        var protectedPassword = password is null ? null : Protect(password);

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
        if (!File.Exists(setupJsonPath)) return false;

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

    // MachineSecret: the same DPAPI call as before on Windows, AES-256-GCM elsewhere. Either way a copy
    // of setup-generated.json taken off this machine doesn't give away the password.
    private static string Protect(string password) => LarisVMS.Core.Security.MachineSecret.ProtectString(password, Entropy);

    private static string Unprotect(string protectedPassword) =>
        LarisVMS.Core.Security.MachineSecret.UnprotectString(protectedPassword, Entropy);
}
