using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// M11: runs BACKUP DATABASE against the app's own database over its existing connection — the whole
/// point being SQL Express installs, which have no SQL Agent to schedule backups with. The .bak is
/// written by the SQL Server service on the SQL host: for the typical same-machine Express setup
/// that's local disk; for a remote SQL Server the path is on that server, and app-side file
/// checks/retention are skipped. Ported from rsolva's BackupService near-verbatim — the only real
/// changes are ISettingsResolver (Global scope) in place of rsolva's flat db.Settings table, and no
/// restore counterpart (deliberately out of scope for this pass; an admin restores by other means).
/// </summary>
public class BackupService(ApplicationDbContext db, ISettingsResolver settings, ILogger<BackupService> logger) : IBackupService
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
            var directoryError = ValidateDirectory(directory);
            if (directoryError != null)
            {
                entry.Status = BackupStatus.Failed;
                entry.Error = directoryError;
                entry.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                return entry;
            }

            var connection = db.Database.GetDbConnection();
            var builder = new SqlConnectionStringBuilder(connection.ConnectionString);
            var databaseName = builder.InitialCatalog;
            var isLocal = IsLocalServer(builder.DataSource);

            var fileName = $"larisvms_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
            var fullPath = Path.Combine(directory!, fileName);

            if (isLocal && !Directory.Exists(directory!))
                Directory.CreateDirectory(directory!);

            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await using var cmd = connection.CreateCommand();
                // BACKUP DATABASE doesn't accept parameters — the path is validated above
                // (rooted, no quote/semicolon/bracket characters) and the db name is bracket-escaped.
                // COMPRESSION is deliberately omitted: SQL Express doesn't support it.
                cmd.CommandText = $"BACKUP DATABASE [{databaseName.Replace("]", "]]")}] " +
                                  $"TO DISK = N'{fullPath.Replace("'", "''")}' WITH INIT";
                cmd.CommandTimeout = 600;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }

            entry.Status = BackupStatus.Success;
            entry.FilePath = fullPath;
            entry.CompletedAt = DateTime.UtcNow;
            if (isLocal && File.Exists(fullPath))
                entry.SizeBytes = new FileInfo(fullPath).Length;
            await db.SaveChangesAsync(CancellationToken.None);

            if (isLocal)
                await ApplyRetentionAsync(directory!, ct);

            logger.LogInformation("Database backup completed: {Path}", fullPath);
            return entry;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Database backup failed.");
            entry.Status = BackupStatus.Failed;
            // Verbatim SQL error — this is where "the SQL Server service account can't write to
            // that folder" surfaces, and the admin needs the real message to fix the ACL.
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

    private async Task ApplyRetentionAsync(string directory, CancellationToken ct)
    {
        var retention = await settings.GetAsync("Backup.RetentionCount", 14, ct: ct);
        if (retention <= 0) retention = 14;

        try
        {
            var backups = new DirectoryInfo(directory)
                .GetFiles("larisvms_*.bak")
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

    internal static string? ValidateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return "No backup directory is configured. Set one on Admin → Backups.";
        if (directory.IndexOfAny(['\'', ';', '[', ']', '\"']) >= 0)
            return "The backup directory contains invalid characters.";
        if (!Path.IsPathRooted(directory))
            return "The backup directory must be an absolute path (e.g. D:\\Backups\\LarisVMS).";
        return null;
    }

    /// <summary>True when the SQL Server in the connection string runs on this machine — the only
    /// case where the app can see the .bak files it asks SQL Server to write.</summary>
    public static bool IsLocalServer(string dataSource)
    {
        var host = dataSource.Split('\\')[0].Split(',')[0].Trim();
        return host is "." or "(local)" or "localhost" or ""
            || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
