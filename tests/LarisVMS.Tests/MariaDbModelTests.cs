using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace LarisVMS.Tests;

/// <summary>MariaDB (experimental) support without a MariaDB server: InnoDB's limits checked against
/// the model the provider builds, the migrations kept in step with it, and the provider plumbing
/// (connection strings, the protected password, mariadb-dump's option file).</summary>
public class MariaDbModelTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();
        MariaDbOps.Instance.Configure(options, "Server=localhost;Database=larisvms;User ID=larisvms");
        return new ApplicationDbContext(options.Options);
    }

    private static IRelationalModel RelationalModel(ApplicationDbContext db) =>
        db.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    private static readonly Regex Sized = new(@"^(?:var)?(?:char|binary)\((\d+)\)$", RegexOptions.Compiled);

    /// <summary>Bytes a column takes in a key; null for a type InnoDB can't index without a prefix.</summary>
    private static int? KeyBytes(IColumn column)
    {
        var type = column.StoreType;
        var sized = Sized.Match(type);
        if (sized.Success)
        {
            var length = int.Parse(sized.Groups[1].Value);
            var bytesPerChar = type.Contains("binary") || (column.Collation?.StartsWith("ascii", StringComparison.Ordinal) ?? false) ? 1 : 4;
            return length * bytesPerChar;
        }
        return type switch
        {
            "tinyint(1)" or "tinyint unsigned" => 1,
            "int" or "float" => 4,
            "bigint" or "double" or "datetime(6)" => 8,
            "time(6)" => 6,
            "date" => 3,
            _ when type.StartsWith("decimal", StringComparison.Ordinal) => 16,
            _ => null // longtext, longblob
        };
    }

    [Fact]
    public void EveryKeyAndIndexFitsInnoDbsKeyLimit()
    {
        using var db = CreateContext();
        var tooLong = new List<string>();
        var checkedKeys = 0;
        foreach (var table in RelationalModel(db).Tables)
        {
            var keys = table.Indexes.Select(i => (i.Name, i.Columns))
                .Concat(table.UniqueConstraints.Select(u => (u.Name, u.Columns)));
            foreach (var (name, columns) in keys)
            {
                checkedKeys++;
                var sizes = columns.Select(KeyBytes).ToList();
                if (sizes.Any(s => s is null) || sizes.Sum() > 3072)
                    tooLong.Add($"{table.Name}.{name}: {string.Join(", ", columns.Select(c => c.Name + " " + c.StoreType))}");
            }
        }
        Assert.Empty(tooLong);
        Assert.True(checkedKeys > 100, $"Only {checkedKeys} keys checked — is the model being built?");
    }

    [Fact]
    public void EveryTableFitsInnoDbsRowSizeLimit()
    {
        using var db = CreateContext();
        var tooWide = RelationalModel(db).Tables
            // longtext/longblob count as their 12-byte pointer; varchar as its full size plus length.
            .Select(t => (t.Name, Bytes: t.Columns.Sum(c => KeyBytes(c) is { } b ? b + (c.StoreType.StartsWith("var", StringComparison.Ordinal) ? 2 : 0) : 12)))
            .Where(t => t.Bytes > 65535)
            .Select(t => $"{t.Name}: {t.Bytes} bytes")
            .ToList();
        Assert.Empty(tooWide);
    }

    [Fact]
    public void MigrationsMatchTheModel()
    {
        // The MariaDB twin of "dotnet ef migrations has-pending-model-changes": a schema change
        // migrated for SQL Server only (tools\add-migration.ps1 makes both) fails here.
        using var db = CreateContext();
        Assert.False(db.Database.HasPendingModelChanges(),
            "The MariaDB model has changes with no migration — run tools\\add-migration.ps1.");
        Assert.NotEmpty(db.Database.GetMigrations());
    }

    [Fact]
    public void SqlServerOnlyIndexOptionsStayOffMariaDb()
    {
        using var db = CreateContext();
        var filtered = db.Model.GetEntityTypes().SelectMany(t => t.GetIndexes()).Where(i => i.GetFilter() is not null).ToList();
        Assert.Empty(filtered);
    }

    [Fact]
    public void ConnectionStringsFollowTheCertificateRule()
    {
        var local = new MySqlConnectionStringBuilder(MariaDbOps.Instance.BuildConnectionString(
            new DatabaseSetupRequest(DatabaseProvider.MariaDb, "localhost", "larisvms", "laris", "pw"), "larisvms"));
        var remote = new MySqlConnectionStringBuilder(MariaDbOps.Instance.BuildConnectionString(
            new DatabaseSetupRequest(DatabaseProvider.MariaDb, "db01.corp.example", "larisvms", "laris", "pw", Port: 3307), null));
        var trusted = new MySqlConnectionStringBuilder(MariaDbOps.Instance.BuildConnectionString(
            new DatabaseSetupRequest(DatabaseProvider.MariaDb, "db01.corp.example", "larisvms", "laris", "pw", TrustServerCertificate: true), null));

        Assert.Equal(MySqlSslMode.Preferred, local.SslMode);
        Assert.Equal("larisvms", local.Database);
        Assert.Equal(MySqlSslMode.VerifyFull, remote.SslMode);
        Assert.Equal(3307u, remote.Port);
        Assert.Equal("", remote.Database);
        Assert.Equal(MySqlSslMode.Required, trusted.SslMode);
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer, "Server=sql01;Database=LarisVMS;User ID=laris;Password=p@ss;Encrypt=True")]
    [InlineData(DatabaseProvider.MariaDb, "Server=db01;Database=larisvms;User ID=laris;Password=p@ss")]
    public void ThePasswordIsSavedApartAndRestored(DatabaseProvider provider, string connectionString)
    {
        var root = new JsonObject();
        var configuration = new ConfigurationManager();
        DatabaseConfig.Save(root, configuration, provider, connectionString);

        Assert.Equal(provider, DatabaseConfig.GetProvider(configuration));
        var ops = DatabaseProviderOps.For(provider);
        ops.WithoutPassword(DatabaseConfig.GetConnectionString(configuration)!, out var restored);
        Assert.Equal("p@ss", restored);

        if (OperatingSystem.IsWindows())
        {
            var saved = root["ConnectionStrings"]!["DefaultConnection"]!.GetValue<string>();
            Assert.DoesNotContain("p@ss", saved);
            Assert.False(string.IsNullOrEmpty(root["Database"]!["ProtectedPassword"]?.GetValue<string>()));
        }
    }

    [Fact]
    public void APlaintextPasswordIsMovedOutOfAnOlderSetupFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), $"setup-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "ConnectionStrings": { "DefaultConnection": "Server=sql01;Database=LarisVMS;User ID=laris;Password=hunter2" },
                  "Branding": { "AppName": "Site" } }
                """);
            Assert.True(DatabaseConfig.ProtectPlaintextPassword(path));
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("hunter2", text);
            Assert.Contains("Site", text);
            Assert.False(DatabaseConfig.ProtectPlaintextPassword(path));

            var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
            Assert.Contains("hunter2", DatabaseConfig.GetConnectionString(configuration));
            Assert.Equal(DatabaseProvider.SqlServer, DatabaseConfig.GetProvider(configuration));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnIntegratedSecurityConnectionHasNoPasswordToProtect()
    {
        var root = new JsonObject();
        var configuration = new ConfigurationManager();
        DatabaseConfig.Save(root, configuration, DatabaseProvider.SqlServer, "Server=.;Database=LarisVMS;Integrated Security=True");
        Assert.Null(root["Database"]!["ProtectedPassword"]);
        Assert.Contains("Integrated Security", DatabaseConfig.GetConnectionString(configuration));
    }

    [Theory]
    [InlineData("larisvms", true)]
    [InlineData("laris_vms-2", true)]
    [InlineData("laris.vms", false)]
    [InlineData("laris vms", false)]
    [InlineData("x`; DROP DATABASE y; --", false)]
    public void MariaDbDatabaseNamesAreRestricted(string name, bool ok)
    {
        var ex = Record.Exception(() => MariaDbOps.Instance.ValidateDatabaseName(name));
        Assert.Equal(ok, ex is null);
    }

    [Fact]
    public void TheDumpOptionFileQuotesTheLoginAndKeepsTheCertificateRule()
    {
        var b = new MySqlConnectionStringBuilder("Server=db01;Port=3307;Database=larisvms;User ID=laris;Password=a\"b\\c;SslMode=VerifyFull");
        var file = BackupService.MariaDbOptionFile(b);
        Assert.StartsWith("[client]\n", file);
        Assert.Contains("password=\"a\\\"b\\\\c\"", file);
        Assert.Contains("port=3307", file);
        Assert.Contains("\nssl-verify-server-cert\n", file);
        Assert.Contains("\nssl\n", file);

        var local = BackupService.MariaDbOptionFile(new MySqlConnectionStringBuilder("Server=localhost;User ID=laris;Password=x;SslMode=Preferred"));
        Assert.Contains("skip-ssl-verify-server-cert", local);
        Assert.DoesNotContain("\nssl\n", local);
    }

    [Fact]
    public void TheDumpOptionFileComesFirstAndNoPasswordIsOnTheCommandLine()
    {
        var args = BackupService.MariaDbDumpArguments(@"C:\ProgramData\LarisVMS\backup.cnf", "larisvms");
        Assert.StartsWith("--defaults-extra-file=", args[0]);
        Assert.DoesNotContain(args, a => a.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--single-transaction", args);
        Assert.Equal("larisvms", args[^1]);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("mariadb-dump.exe", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    public void OnlyAMariaDbDumpExecutableIsAccepted(string path, bool ok) =>
        Assert.Equal(ok, BackupService.ValidateMariaDbDumpPath(path) is null);

    [Fact]
    public void BothServersUniqueViolationsAreRecognized()
    {
        Assert.False(DatabaseProviderOps.IsUniqueViolation(new InvalidOperationException()));
        Assert.False(DatabaseProviderOps.IsUniqueViolation(null));
    }

    [Fact]
    public async Task TheReleaseIsRecordedOnce()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options);
        await AppRelease.EnsureRecordedAsync(db);
        await AppRelease.EnsureRecordedAsync(db);
        var row = Assert.Single(db.AppVersions);
        Assert.Equal(AppRelease.Version, row.ToVersionString());
        Assert.Equal(AppRelease.Notes, row.Notes);
    }
}
