using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>
/// M11: schedule/trigger/history for BACKUP DATABASE runs — ported from rsolva's Pages/Admin/Backup
/// UI, adapted to a single Backups.Edit permission (this app doesn't split View/Modify within one
/// admin page anywhere else — see Admin/Nodes' own comment on why) and ISettingsResolver in place of
/// rsolva's flat db.Settings. No restore action here by design (see BackupService's doc comment).
///
/// Kept on its own Backups.Edit gate rather than folded under the rest of Settings' Settings.Edit —
/// moved here from Admin/Backup as one tab of the combined Settings page, not merged into its
/// permission model.
/// </summary>
[Authorize("Backups.Edit")]
public class BackupsModel(ApplicationDbContext db, IBackupService backupService, ISettingsResolver settings, IAuditService auditService,
    IPermissionService permissions, IConfiguration configuration, LarisVMS.Web.Services.INotificationService notifications) : PageModel
{
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public List<BackupHistoryEntry> History { get; set; } = [];
    public bool IsRemoteSql { get; set; }
    public string? DataSource { get; set; }
    public bool IsMariaDb { get; set; }
    /// <summary>Where mariadb-dump will be run from, when it was found; MariaDB only.</summary>
    public string? DetectedDumpPath { get; set; }

    [BindProperty] public bool BackupEnabled { get; set; }
    [BindProperty] public string BackupTime { get; set; } = "03:00";
    [BindProperty] public string? BackupDirectory { get; set; }
    [BindProperty] public string? EncryptionCertificate { get; set; }
    [BindProperty] public int RetentionCount { get; set; } = 14;
    [BindProperty] public string? MariaDbDumpPath { get; set; }

    public async Task OnGetAsync()
    {
        BackupEnabled = await settings.GetAsync("Backup.Enabled", false);
        BackupTime = await settings.GetRawAsync("Backup.Time") ?? "03:00";
        BackupDirectory = await settings.GetRawAsync("Backup.Directory");
        EncryptionCertificate = await settings.GetRawAsync(LarisVMS.Infrastructure.Services.BackupService.EncryptionCertificateKey);
        RetentionCount = await settings.GetAsync("Backup.RetentionCount", 14);
        if (RetentionCount <= 0) RetentionCount = 14;

        MariaDbDumpPath = await settings.GetRawAsync(BackupService.MariaDbDumpPathKey);

        var cs = db.Database.GetConnectionString();
        if (!string.IsNullOrEmpty(cs))
        {
            var ops = DatabaseConfig.GetOps(configuration);
            IsMariaDb = ops is MariaDbOps;
            DataSource = ops.DescribeServer(cs);
            // mariadb-dump runs on this machine, so where the server is doesn't matter.
            IsRemoteSql = !IsMariaDb && !ops.IsLocalServer(cs);
            if (IsMariaDb && string.IsNullOrWhiteSpace(MariaDbDumpPath))
                DetectedDumpPath = BackupService.FindMariaDbDump(null) is { } found && System.IO.File.Exists(found) ? found : null;
        }

        History = await db.BackupHistoryEntries
            .OrderByDescending(b => b.StartedAt)
            .Take(20)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!TimeOnly.TryParse(BackupTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            ErrorMessage = "Enter the backup time as HH:mm (e.g. 03:00).";
            return RedirectToPage();
        }
        if (BackupEnabled && string.IsNullOrWhiteSpace(BackupDirectory))
        {
            ErrorMessage = "A backup directory is required when scheduled backups are enabled.";
            return RedirectToPage();
        }

        var by = User.Identity?.Name;

        // Pre-save values, so the entry reports what changed rather than restating every field on
        // every save (which is what the old flat "enabled=…, time=…, …" string did — it could never
        // answer "what did they actually change?"). No secret fields here: a backup directory path
        // is not a credential.
        var oldEnabled = await settings.GetAsync("Backup.Enabled", false);
        var oldTime = await settings.GetRawAsync("Backup.Time") ?? "03:00";
        var oldDirectory = await settings.GetRawAsync("Backup.Directory");
        var oldRetention = await settings.GetAsync("Backup.RetentionCount", 14);
        var oldCertificate = await settings.GetRawAsync(LarisVMS.Infrastructure.Services.BackupService.EncryptionCertificateKey);
        var oldDumpPath = await settings.GetRawAsync(BackupService.MariaDbDumpPathKey);

        // Where the whole database goes, and whether it's encrypted, is a Super Admin's call: a
        // folder or share someone else controls would hand them a copy of everything.
        var directoryChanged = !string.Equals(oldDirectory?.Trim() ?? "", BackupDirectory?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
        var certificateChanged = !string.Equals(oldCertificate?.Trim() ?? "", EncryptionCertificate?.Trim() ?? "", StringComparison.Ordinal);
        // The dump tool is a program this service runs under its own account.
        var dumpPathChanged = !string.Equals(oldDumpPath?.Trim() ?? "", MariaDbDumpPath?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
        if ((directoryChanged || certificateChanged || dumpPathChanged) && !(await permissions.GetGrantedAsync(User)).IsAdministrator)
        {
            ErrorMessage = "Only a Super Admin can change the backup folder, its encryption, or the dump tool.";
            return RedirectToPage();
        }
        if (directoryChanged && !string.IsNullOrWhiteSpace(BackupDirectory)
            && LarisVMS.Infrastructure.Services.BackupService.AllowedRoots(configuration) is { Length: > 0 } roots
            && !LarisVMS.Infrastructure.Services.BackupService.IsUnderAny(BackupDirectory, roots))
        {
            ErrorMessage = $"Backups can only go to: {string.Join(", ", roots)}.";
            return RedirectToPage();
        }
        if (!string.IsNullOrWhiteSpace(EncryptionCertificate) && !LarisVMS.Infrastructure.Services.BackupService.IsValidCertificateName(EncryptionCertificate.Trim()))
        {
            ErrorMessage = "Enter the certificate's name as created in SQL Server (letters, digits, spaces, _ and -).";
            return RedirectToPage();
        }

        if (dumpPathChanged && BackupService.ValidateMariaDbDumpPath(MariaDbDumpPath) is { } dumpPathError)
        {
            ErrorMessage = dumpPathError;
            return RedirectToPage();
        }

        var newRetention = Math.Max(1, RetentionCount);
        await settings.SetGlobalAsync("Backup.Enabled", BackupEnabled.ToString(), by);
        await settings.SetGlobalAsync("Backup.Time", BackupTime.Trim(), by);
        await settings.SetGlobalAsync("Backup.Directory", BackupDirectory?.Trim() ?? string.Empty, by);
        await settings.SetGlobalAsync("Backup.RetentionCount", newRetention.ToString(), by);
        await settings.SetGlobalAsync(LarisVMS.Infrastructure.Services.BackupService.EncryptionCertificateKey, EncryptionCertificate?.Trim() ?? string.Empty, by);
        if (dumpPathChanged)
            await settings.SetGlobalAsync(BackupService.MariaDbDumpPathKey, MariaDbDumpPath?.Trim() ?? string.Empty, by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Enabled", oldEnabled.ToString(), BackupEnabled.ToString()),
            AuditDiff.Of("Time", oldTime, BackupTime.Trim()),
            AuditDiff.Of("Directory", oldDirectory, BackupDirectory?.Trim()),
            AuditDiff.Of("Retention", oldRetention.ToString(), newRetention.ToString()),
            AuditDiff.Of("Encryption certificate", oldCertificate, EncryptionCertificate?.Trim()),
            AuditDiff.Of("Dump tool", oldDumpPath, dumpPathChanged ? MariaDbDumpPath?.Trim() : oldDumpPath));

        await auditService.LogAsync("Backup.SettingsUpdate",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        // Where the whole database goes is worth every Super Admin knowing about.
        if (directoryChanged || certificateChanged || dumpPathChanged)
        {
            var dumpTool = string.IsNullOrWhiteSpace(MariaDbDumpPath) ? "the one found automatically" : MariaDbDumpPath.Trim();
            string what;
            if (directoryChanged) what = $"the backup folder to {BackupDirectory?.Trim()}";
            else if (certificateChanged) what = "backup encryption";
            else what = $"the backup dump tool to {dumpTool}";
            await notifications.NotifySuperAdminsAsync("Backup settings changed", $"{by ?? "Someone"} changed {what}.",
                "/Admin/Settings/Backups");
        }

        Message = "Backup settings saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRunNowAsync()
    {
        var entry = await backupService.RunBackupAsync("manual");

        await auditService.LogAsync("Backup.ManualRun",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(), $"{entry.Status} — {entry.FilePath ?? entry.Error}");

        if (entry.Status == BackupStatus.Success)
            Message = $"Backup completed: {entry.FilePath}";
        else
            ErrorMessage = $"Backup failed: {entry.Error}";
        return RedirectToPage();
    }
}
