using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
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
public class BackupsModel(ApplicationDbContext db, IBackupService backupService, ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [TempData] public string? Message { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public List<BackupHistoryEntry> History { get; set; } = [];
    public bool IsRemoteSql { get; set; }
    public string? DataSource { get; set; }

    [BindProperty] public bool BackupEnabled { get; set; }
    [BindProperty] public string BackupTime { get; set; } = "03:00";
    [BindProperty] public string? BackupDirectory { get; set; }
    [BindProperty] public int RetentionCount { get; set; } = 14;

    public async Task OnGetAsync()
    {
        BackupEnabled = await settings.GetAsync("Backup.Enabled", false);
        BackupTime = await settings.GetRawAsync("Backup.Time") ?? "03:00";
        BackupDirectory = await settings.GetRawAsync("Backup.Directory");
        RetentionCount = await settings.GetAsync("Backup.RetentionCount", 14);
        if (RetentionCount <= 0) RetentionCount = 14;

        var cs = db.Database.GetConnectionString();
        if (!string.IsNullOrEmpty(cs))
        {
            DataSource = new SqlConnectionStringBuilder(cs).DataSource;
            IsRemoteSql = !BackupService.IsLocalServer(DataSource);
        }

        History = await db.BackupHistoryEntries
            .OrderByDescending(b => b.StartedAt)
            .Take(20)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (!TimeOnly.TryParse(BackupTime, out _))
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

        var newRetention = Math.Max(1, RetentionCount);
        await settings.SetGlobalAsync("Backup.Enabled", BackupEnabled.ToString(), by);
        await settings.SetGlobalAsync("Backup.Time", BackupTime.Trim(), by);
        await settings.SetGlobalAsync("Backup.Directory", BackupDirectory?.Trim() ?? string.Empty, by);
        await settings.SetGlobalAsync("Backup.RetentionCount", newRetention.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Enabled", oldEnabled.ToString(), BackupEnabled.ToString()),
            AuditDiff.Of("Time", oldTime, BackupTime.Trim()),
            AuditDiff.Of("Directory", oldDirectory, BackupDirectory?.Trim()),
            AuditDiff.Of("Retention", oldRetention.ToString(), newRetention.ToString()));

        await auditService.LogAsync("Backup.SettingsUpdate",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

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
