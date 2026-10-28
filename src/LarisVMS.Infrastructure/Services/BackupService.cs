using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// M11: runs BACKUP DATABASE (SQL Server) or mariadb-dump (MariaDB, experimental) against the app's own database over its existing connection — the whole
/// point being SQL Express installs, which have no SQL Agent to schedule backups with. The .bak is
/// written by the SQL Server service on the SQL host: for the typical same-machine Express setup
/// that's local disk; for a remote SQL Server the path is on that server, and app-side file
/// checks/retention are skipped. Ported from rsolva's BackupService near-verbatim — the only real
/// changes are ISettingsResolver (Global scope) in place of rsolva's flat db.Settings table, and no
/// restore counterpart (deliberately out of scope for this pass; an admin restores by other means).
/// </summary>
public class BackupService(ApplicationDbContext db, ISettingsResolver settings, ILogger<BackupService> logger,
    Microsoft.Extensions.Configuration.IConfiguration? configuration = null) : IBackupService
{
    // One backup at a time, app-wide.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<BackupHistoryEntry> RunBackupAsync(string triggeredBy, CancellationToken ct = default)
    {
        var entry = new BackupHistoryEntry { TriggeredBy = triggeredBy, Status = BackupStatus.Running };

        if (!await Gate.WaitAsync(0, ct))
        {
            entry.Status = BackupStatus.Failed;
            entry.Error = "A backup is already running.";
            entry.CompletedAt = DateTime.UtcNow;
            db.BackupHistoryEntries.Add(entry);
            await db.SaveChangesAsync(ct);
            return entry;
        }

        try
        {
            db.BackupHistoryEntries.Add(entry);
            await db.SaveChangesAsync(ct);

            var directory = (await settings.GetRawAsync("Backup.Directory", ct: ct))?.Trim();
            var directoryError = ValidateDirectory(directory, AllowedRoots(configuration));
            if (directoryError != null)
            {
                entry.Status = BackupStatus.Failed;
                entry.Error = directoryError;
                entry.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                return entry;
            }

            var connection = db.Database.GetDbConnection();
            var mariaDb = !db.Database.IsSqlServer();
            // mariadb-dump runs here and writes here, so its files are always visible to the app;
            // BACKUP DATABASE writes on the SQL Server's own machine.
            var isLocal = mariaDb || IsLocalServer(new SqlConnectionStringBuilder(connection.ConnectionString).DataSource);

            var fileName = $"larisvms_{DateTime.UtcNow:yyyyMMdd_HHmmss}{(mariaDb ? MariaDbExtension : ".bak")}";
            var fullPath = Path.Combine(directory!, fileName);

            if (isLocal && !Directory.Exists(directory!))
                Directory.CreateDirectory(directory!);

            if (mariaDb)
                await RunMariaDbDumpAsync((configuration is null ? null : DatabaseConfig.GetConnectionString(configuration)) ?? connection.ConnectionString, fullPath, ct);
            else
                await RunSqlServerBackupAsync(connection, new SqlConnectionStringBuilder(connection.ConnectionString).InitialCatalog, fullPath, ct);

            entry.Status = BackupStatus.Success;
            entry.FilePath = fullPath;
            entry.CompletedAt = DateTime.UtcNow;
            if (isLocal && File.Exists(fullPath))
                entry.SizeBytes = new FileInfo(fullPath).Length;
            await db.SaveChangesAsync(CancellationToken.None);

            if (isLocal)
                await ApplyRetentionAsync(directory!, mariaDb ? "larisvms_*" + MariaDbExtension : "larisvms_*.bak", ct);

            logger.LogInformation("Database backup completed: {Path}", fullPath);
            return entry;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Database backup failed.");
            entry.Status = BackupStatus.Failed;
            // Verbatim error — this is where "the SQL Server service account can't write to that
            // folder" (or mariadb-dump's own complaint) surfaces, and the admin needs the real message.
            entry.Error = ex.Message[..Math.Min(ex.Message.Length, 2000)];
            entry.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return entry;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task RunSqlServerBackupAsync(System.Data.Common.DbConnection connection, string databaseName, string fullPath, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            // BACKUP DATABASE doesn't accept parameters — the path is validated above
            // (rooted, no quote/semicolon/bracket characters) and the db name is bracket-escaped.
            // COMPRESSION is deliberately omitted: SQL Express doesn't support it.
            // With a server certificate named in settings (created in SQL Server by its admin), the
            // .bak is encrypted, so a copy taken elsewhere is useless without that certificate.
            var certificate = (await settings.GetRawAsync(EncryptionCertificateKey, ct: ct))?.Trim();
            var encryption = IsValidCertificateName(certificate)
                ? $", ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = [{certificate!.Replace("]", "]]")}])"
                : "";
            cmd.CommandText = $"BACKUP DATABASE [{databaseName.Replace("]", "]]")}] " +
                              $"TO DISK = N'{fullPath.Replace("'", "''")}' WITH INIT{encryption}";
            cmd.CommandTimeout = 600;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public const string MariaDbExtension = ".sql.gz";
    public const string MariaDbDumpPathKey = "Backup.MariaDbDumpPath";

    /// <summary>Null when <paramref name="path"/> is acceptable as the dump tool: blank (find it), or
    /// an existing mariadb-dump/mysqldump executable given by its full path. The service runs it
    /// under its own account, so nothing else is accepted.</summary>
    public static string? ValidateMariaDbDumpPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        if (!Path.IsPathRooted(path))
            return @"Enter the full path to mariadb-dump (e.g. C:\Program Files\MariaDB 11.4\bin\mariadb-dump.exe).";
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.Equals("mariadb-dump", StringComparison.OrdinalIgnoreCase) && !name.Equals("mysqldump", StringComparison.OrdinalIgnoreCase))
            return "The dump tool must be mariadb-dump or mysqldump.";
        return File.Exists(path) ? null : "That file doesn't exist on this server.";
    }

    /// <summary>MariaDB has no BACKUP DATABASE: runs mariadb-dump on this machine and gzips its output.
    /// The login goes in an option file under %ProgramData%\LarisVMS (SYSTEM, Administrators, and this
    /// service only) rather than on the command line, where any local user could read it.</summary>
    private async Task RunMariaDbDumpAsync(string connectionString, string fullPath, CancellationToken ct)
    {
        var tool = FindMariaDbDump(await settings.GetRawAsync(MariaDbDumpPathKey, ct: ct))
            ?? throw new InvalidOperationException(
                "mariadb-dump wasn't found at the path set on Admin → Backups.");

        var b = new MySqlConnector.MySqlConnectionStringBuilder(connectionString);
        Directory.CreateDirectory(LarisVMS.Core.Security.LocalDataAcl.Root);
        var optionFile = Path.Combine(LarisVMS.Core.Security.LocalDataAcl.Root, $"backup-{Guid.NewGuid():N}.cnf");
        try
        {
            await File.WriteAllTextAsync(optionFile, MariaDbOptionFile(b), ct);
            LarisVMS.Core.Security.LocalDataAcl.ProtectFile(optionFile);

            var psi = new System.Diagnostics.ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in MariaDbDumpArguments(optionFile, b.Database)) psi.ArgumentList.Add(arg);

            System.Diagnostics.Process process;
            try
            {
                process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {tool}.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not run {tool} ({ex.Message}). Install the MariaDB client tools on this machine, or set the path to mariadb-dump on Admin → Backups.", ex);
            }

            using (process)
            {
                var stderr = process.StandardError.ReadToEndAsync(ct);
                try
                {
                    await using (var file = File.Create(fullPath))
                    await using (var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
                        await process.StandardOutput.BaseStream.CopyToAsync(gzip, ct);
                    await process.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { /* already exited */ }
                    File.Delete(fullPath);
                    throw;
                }

                if (process.ExitCode != 0)
                {
                    File.Delete(fullPath);
                    throw new InvalidOperationException($"mariadb-dump exited with code {process.ExitCode}: {(await stderr).Trim()}");
                }
            }
        }
        finally
        {
            File.Delete(optionFile);
        }
    }

    internal static IReadOnlyList<string> MariaDbDumpArguments(string optionFile, string database) =>
    [
        // Must come first, or the tool ignores it.
        "--defaults-extra-file=" + optionFile,
        // One consistent snapshot without locking the tables the app is writing to.
        "--single-transaction",
        "--routines",
        "--triggers",
        "--hex-blob",
        "--default-character-set=utf8mb4",
        "--databases", database
    ];

    internal static string MariaDbOptionFile(MySqlConnector.MySqlConnectionStringBuilder b)
    {
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var lines = new List<string>
        {
            "[client]",
            "host=" + Quote(b.Server),
            "port=" + (b.Port == 0 ? 3306 : b.Port),
            "user=" + Quote(b.UserID),
            "password=" + Quote(b.Password),
            // Same certificate rule as the app's own connection (MariaDbOps.BuildConnectionString).
            b.SslMode is MySqlConnector.MySqlSslMode.VerifyFull or MySqlConnector.MySqlSslMode.VerifyCA
                ? "ssl-verify-server-cert"
                : "skip-ssl-verify-server-cert"
        };
        if (b.SslMode is not (MySqlConnector.MySqlSslMode.None or MySqlConnector.MySqlSslMode.Preferred))
            lines.Add("ssl");
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>The configured path (null when it doesn't exist), else the newest MariaDB install
    /// under Program Files, else whatever is on PATH.</summary>
    public static string? FindMariaDbDump(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured.Trim()) ? configured.Trim() : null;

        foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(p => p.Length > 0).Distinct())
        {
            if (!Directory.Exists(programFiles)) continue;
            var found = Directory.GetDirectories(programFiles, "MariaDB*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .SelectMany(d => new[] { Path.Combine(d, "bin", "mariadb-dump.exe"), Path.Combine(d, "bin", "mysqldump.exe") })
                .FirstOrDefault(File.Exists);
            if (found is not null) return found;
        }
        return OperatingSystem.IsWindows() ? "mariadb-dump.exe" : "mariadb-dump";
    }

    private async Task ApplyRetentionAsync(string directory, string pattern, CancellationToken ct)
    {
        var retention = await settings.GetAsync("Backup.RetentionCount", 14, ct: ct);
        if (retention <= 0) retention = 14;

        try
        {
            var backups = new DirectoryInfo(directory)
                .GetFiles(pattern)
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();
            foreach (var old in backups.Skip(retention))
                old.Delete();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Backup retention cleanup failed in {Directory}", directory);
        }
    }

    public const string EncryptionCertificateKey = "Backup.EncryptionCertificate";

    /// <summary>Backups:AllowedRoots in appsettings (set by whoever runs the server, not from the web
    /// UI): when present, backups may only go to these folders or shares, or below them.</summary>
    public static string[] AllowedRoots(Microsoft.Extensions.Configuration.IConfiguration? configuration) =>
        configuration?.GetSection("Backups:AllowedRoots").GetChildren().Select(c => c.Value ?? "")
            .Where(v => v.Length > 0).ToArray() ?? [];

    public static bool IsValidCertificateName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or ' ');

    internal static string? ValidateDirectory(string? directory, IReadOnlyList<string>? allowedRoots = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return "No backup directory is configured. Set one on Admin → Backups.";
        if (directory.IndexOfAny(['\'', ';', '[', ']', '\"']) >= 0)
            return "The backup directory contains invalid characters.";
        if (!Path.IsPathRooted(directory))
            return "The backup directory must be an absolute path (e.g. D:\\Backups\\LarisVMS).";
        if (allowedRoots is { Count: > 0 } && !IsUnderAny(directory, allowedRoots))
            return "That backup folder isn't one of the allowed backup locations (Backups:AllowedRoots).";
        return null;
    }

    /// <summary>Whether <paramref name="directory"/> is one of <paramref name="roots"/> or below one.</summary>
    public static bool IsUnderAny(string directory, IReadOnlyList<string> roots)
    {
        static string Norm(string p) => p.Trim().Replace('/', '\\').TrimEnd('\\') + "\\";
        return roots.Any(root => Norm(directory).StartsWith(Norm(root), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the SQL Server in the connection string runs on this machine — the only
    /// case where the app can see the .bak files it asks SQL Server to write.</summary>
    public static bool IsLocalServer(string dataSource) => SqlServerOps.IsLocalDataSource(dataSource);
}
