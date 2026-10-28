using System.Data.Common;
using System.Text.RegularExpressions;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace LarisVMS.Infrastructure.Data;

/// <summary>
/// Everything that differs between the database servers LarisVMS can run on: connection strings,
/// creating the database, the raw connections opened before DI exists, and the EF provider setup.
/// The rest of the app goes through EF and doesn't care. Static instances, since Program.cs needs
/// them before the container is built — see <see cref="DatabaseConfig.GetOps"/>.
/// </summary>
public abstract class DatabaseProviderOps
{
    public static DatabaseProviderOps For(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.MariaDb => MariaDbOps.Instance,
        _ => SqlServerOps.Instance
    };

    public abstract DatabaseProvider Provider { get; }

    /// <summary>A connection string for <paramref name="database"/>, or for the server itself (to
    /// create the database) when it's null.</summary>
    public abstract string BuildConnectionString(DatabaseSetupRequest request, string? database);

    public abstract void Configure(DbContextOptionsBuilder options, string connectionString);

    public abstract DbConnection CreateConnection(string connectionString, int? connectTimeoutSeconds = null);

    public abstract string QuoteIdentifier(string name);

    /// <summary>Throws when the name can't safely be used as a database name on this server.</summary>
    public abstract void ValidateDatabaseName(string name);

    public abstract Task<bool> DatabaseExistsAsync(DbConnection server, string database, CancellationToken ct);
    public abstract Task CreateDatabaseAsync(DbConnection server, string database, CancellationToken ct);
    public abstract Task<int> CountUserTablesAsync(DbConnection database, CancellationToken ct);

    /// <summary>Gives the account this service runs as access to the database, where the server
    /// knows Windows accounts at all. Best-effort.</summary>
    public virtual Task GrantServiceAccountAsync(DatabaseSetupRequest request, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The server, for log lines and the Backups page.</summary>
    public abstract string DescribeServer(string connectionString);
    public abstract string DatabaseName(string connectionString);

    /// <summary>True when the database server runs on this machine, so files it writes are visible here.</summary>
    public abstract bool IsLocalServer(string connectionString);

    /// <summary>The connection string with its password taken out, and the password.</summary>
    public abstract string WithoutPassword(string connectionString, out string? password);
    public abstract string WithPassword(string connectionString, string password);

    public static bool IsUniqueViolation(Exception? ex) => ex switch
    {
        SqlException { Number: 2601 or 2627 } => true,
        MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry } => true,
        _ => false
    };

    // Database/login names are meant to be plain identifiers, validated up front against an
    // allow-list (Windows account names additionally need '\' for DOMAIN\user) so a crafted name
    // can't break out of the quoted identifier or string literal contexts they're used in.
    internal static readonly Regex SqlIdentifierPattern =
        new(@"^[A-Za-z0-9_][A-Za-z0-9_ \-\.\$\\]*$", RegexOptions.Compiled);

    internal static bool IsThisMachine(string host) =>
        host is "." or "(local)" or "(localdb)" or "127.0.0.1" or "::1" or ""
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}

public sealed class SqlServerOps : DatabaseProviderOps
{
    public static readonly SqlServerOps Instance = new();

    public override DatabaseProvider Provider => DatabaseProvider.SqlServer;

    public override string BuildConnectionString(DatabaseSetupRequest request, string? database)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = request.Server,
            InitialCatalog = database ?? "master",
            Encrypt = true,
            // The certificate is checked unless the admin said not to, or SQL Server is on this
            // machine (nothing in between to intercept; SQL Express's own certificate is self-signed).
            TrustServerCertificate = request.TrustServerCertificate || IsThisMachineServer(request.Server)
        };

        if (!string.IsNullOrWhiteSpace(request.Username) && !string.IsNullOrWhiteSpace(request.Password))
        {
            b.UserID = request.Username;
            b.Password = request.Password;
        }
        else
        {
            b.IntegratedSecurity = true;
        }

        return b.ConnectionString;
    }

    internal static bool IsThisMachineServer(string server)
    {
        var host = server.Split('\\', ',')[0].Trim();
        if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return IsThisMachine(host);
    }

    // Split-query by default: the one multi-collection projection in the app
    // (CameraService.ProjectWithoutCredentials — Groups + Streams) is far better served by two
    // round-trips than a single cartesian join, and it's on hot paths (dashboard, playback,
    // snapshots, /live). Single-collection Includes elsewhere are unaffected in practice, and EF
    // adds its own PK ordering to correlate the split results, so nothing needs a manual OrderBy.
    public override void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlServer(connectionString, sql => sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));

    public override DbConnection CreateConnection(string connectionString, int? connectTimeoutSeconds = null) =>
        new SqlConnection(connectTimeoutSeconds is { } timeout
            ? new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = timeout }.ConnectionString
            : connectionString);

    public override string QuoteIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";

    public override void ValidateDatabaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !SqlIdentifierPattern.IsMatch(name))
            throw new ArgumentException("The database name contains characters that aren't allowed in a SQL identifier.");
    }

    public override async Task<bool> DatabaseExistsAsync(DbConnection server, string database, CancellationToken ct)
    {
        await using var command = server.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{EscapeLiteral(database)}'";
        return ((int?)await command.ExecuteScalarAsync(ct) ?? 0) > 0;
    }

    public override async Task CreateDatabaseAsync(DbConnection server, string database, CancellationToken ct)
    {
        await using var command = server.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(database)} COLLATE Latin1_General_100_CI_AS_SC_UTF8";
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<int> CountUserTablesAsync(DbConnection database, CancellationToken ct)
    {
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0";
        return (int)(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public override async Task GrantServiceAccountAsync(DatabaseSetupRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
#pragma warning disable CA1416
            var account = System.Security.Principal.WindowsIdentity.GetCurrent()?.Name;
#pragma warning restore CA1416
            if (account is null || !SqlIdentifierPattern.IsMatch(account)) return;

            await using var conn = new SqlConnection(BuildConnectionString(request, null));
            await conn.OpenAsync(ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{EscapeLiteral(account)}')
                        CREATE LOGIN {QuoteIdentifier(account)} FROM WINDOWS;";
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await conn.ChangeDatabaseAsync(request.DatabaseName, ct);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{EscapeLiteral(account)}')
                        CREATE USER {QuoteIdentifier(account)} FOR LOGIN {QuoteIdentifier(account)};
                    IF NOT EXISTS (
                        SELECT 1 FROM sys.database_role_members
                        WHERE member_principal_id = DATABASE_PRINCIPAL_ID(N'{EscapeLiteral(account)}')
                          AND role_principal_id   = DATABASE_PRINCIPAL_ID(N'db_owner'))
                        ALTER ROLE db_owner ADD MEMBER {QuoteIdentifier(account)};";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: could not grant service account permissions: {ex.Message}");
        }
    }

    public override string DescribeServer(string connectionString) => new SqlConnectionStringBuilder(connectionString).DataSource;
    public override string DatabaseName(string connectionString) => new SqlConnectionStringBuilder(connectionString).InitialCatalog;

    public override bool IsLocalServer(string connectionString) => IsLocalDataSource(DescribeServer(connectionString));

    internal static bool IsLocalDataSource(string dataSource)
    {
        var host = dataSource.Split('\\')[0].Split(',')[0].Trim();
        return host is "." or "(local)" or "localhost" or ""
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    public override string WithoutPassword(string connectionString, out string? password)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        password = string.IsNullOrEmpty(b.Password) ? null : b.Password;
        b.Remove("Password");
        return b.ConnectionString;
    }

    public override string WithPassword(string connectionString, string password) =>
        new SqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;

    private static string EscapeLiteral(string input) => input.Replace("'", "''");
}

/// <summary>MariaDB (experimental). SQL logins only — the server has no notion of the Windows
/// account this service runs as, so the wizard always asks for a user and password.</summary>
public sealed class MariaDbOps : DatabaseProviderOps
{
    public static readonly MariaDbOps Instance = new();

    public const string MigrationsAssembly = "LarisVMS.Infrastructure.MariaDb";

    /// <summary>The oldest server the app supports (10.11 LTS). Fixed rather than detected, so
    /// building a context never needs a round-trip and migrations are generated the same everywhere.</summary>
    public static readonly ServerVersion ServerVersion = new MariaDbServerVersion(new Version(10, 11, 0));

    /// <summary>Case-insensitive and accent-sensitive, like SQL Server's Latin1_General_100_CI_AS_SC_UTF8.</summary>
    public const string Collation = "utf8mb4_uca1400_as_ci";

    public override DatabaseProvider Provider => DatabaseProvider.MariaDb;

    public override string BuildConnectionString(DatabaseSetupRequest request, string? database)
    {
        var b = new MySqlConnectionStringBuilder
        {
            Server = request.Server,
            Port = (uint)(request.Port ?? 3306),
            UserID = request.Username ?? "",
            Password = request.Password ?? "",
            // Same rule as SQL Server: the certificate is checked unless the admin said not to, or the
            // server is on this machine — where TLS is only used if the server offers it, since
            // MariaDB before 11.4 has no certificate until one is set up.
            SslMode = IsThisMachine(request.Server.Trim()) ? MySqlSslMode.Preferred
                : request.TrustServerCertificate ? MySqlSslMode.Required
                : MySqlSslMode.VerifyFull
        };
        if (database is not null) b.Database = database;
        return b.ConnectionString;
    }

    public override void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseMySql(connectionString, ServerVersion, mysql => mysql
            .MigrationsAssembly(MigrationsAssembly)
            .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));

    public override DbConnection CreateConnection(string connectionString, int? connectTimeoutSeconds = null) =>
        new MySqlConnection(connectTimeoutSeconds is { } timeout
            ? new MySqlConnectionStringBuilder(connectionString) { ConnectionTimeout = (uint)timeout }.ConnectionString
            : connectionString);

    public override string QuoteIdentifier(string name) => "`" + name.Replace("`", "``") + "`";

    // Stricter than SQL Server: MariaDB maps a database to a folder, so no '.', '\', or spaces.
    private static readonly Regex DatabaseNamePattern = new(@"^[A-Za-z0-9_$][A-Za-z0-9_$\-]{0,63}$", RegexOptions.Compiled);

    public override void ValidateDatabaseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !DatabaseNamePattern.IsMatch(name))
            throw new ArgumentException("A MariaDB database name can only use letters, digits, '_', '$', and '-' (up to 64 characters).");
    }

    public override async Task<bool> DatabaseExistsAsync(DbConnection server, string database, CancellationToken ct)
    {
        await using var command = server.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @name";
        AddParameter(command, "@name", database);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? 0L) > 0;
    }

    public override async Task CreateDatabaseAsync(DbConnection server, string database, CancellationToken ct)
    {
        await using var command = server.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(database)} CHARACTER SET utf8mb4 COLLATE {Collation}";
        await command.ExecuteNonQueryAsync(ct);
    }

    public override async Task<int> CountUserTablesAsync(DbConnection database, CancellationToken ct)
    {
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public override string DescribeServer(string connectionString)
    {
        var b = new MySqlConnectionStringBuilder(connectionString);
        return b.Port is 3306 or 0 ? b.Server : $"{b.Server}:{b.Port}";
    }

    public override string DatabaseName(string connectionString) => new MySqlConnectionStringBuilder(connectionString).Database;

    public override bool IsLocalServer(string connectionString) =>
        IsThisMachine(new MySqlConnectionStringBuilder(connectionString).Server.Trim());

    public override string WithoutPassword(string connectionString, out string? password)
    {
        var b = new MySqlConnectionStringBuilder(connectionString);
        password = string.IsNullOrEmpty(b.Password) ? null : b.Password;
        b.Remove("Password");
        return b.ConnectionString;
    }

    public override string WithPassword(string connectionString, string password) =>
        new MySqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
