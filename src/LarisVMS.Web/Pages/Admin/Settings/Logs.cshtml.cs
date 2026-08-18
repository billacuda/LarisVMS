using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Helpers;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Retention for the two log-shaped things this app keeps: its own application log
/// (LogsRetentionService) and the audit trail (AuditLogRetentionService) — plus a read-only display
/// of where the application log actually lives. The path can't be a normal editable field here: it's
/// read by Program.cs before the DI container (and so ISettingsResolver) exists, specifically so a
/// startup failure still gets written to disk — see LogPaths' own doc comment.</summary>
[Authorize("Settings.Edit")]
public class LogsModel(ISettingsResolver settings, IAuditService auditService, IConfiguration configuration) : PageModel
{
    [BindProperty] public int LogRetentionDays { get; set; } = LogsRetentionService.DefaultRetentionDays;
    [BindProperty] public int AuditLogRetentionDays { get; set; } = AuditLogRetentionService.DefaultRetentionDays;

    public string LogPath { get; set; } = string.Empty;
    public string? ConfiguredLogPathOverride { get; set; }

    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        LogRetentionDays = await settings.GetAsync(LogsRetentionService.RetentionDaysKey, LogsRetentionService.DefaultRetentionDays);
        AuditLogRetentionDays = await settings.GetAsync(AuditLogRetentionService.RetentionDaysKey, AuditLogRetentionService.DefaultRetentionDays);
        LogPath = LogPaths.AppLogsDirectory(configuration);
        ConfiguredLogPathOverride = configuration[LogPaths.ConfigKey];
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;

        var oldLogRetention = await settings.GetAsync(LogsRetentionService.RetentionDaysKey, LogsRetentionService.DefaultRetentionDays);
        var oldAuditRetention = await settings.GetAsync(AuditLogRetentionService.RetentionDaysKey, AuditLogRetentionService.DefaultRetentionDays);

        var newLogRetention = Math.Max(0, LogRetentionDays);
        var newAuditRetention = Math.Max(0, AuditLogRetentionDays);
        await settings.SetGlobalAsync(LogsRetentionService.RetentionDaysKey, newLogRetention.ToString(), by);
        await settings.SetGlobalAsync(AuditLogRetentionService.RetentionDaysKey, newAuditRetention.ToString(), by);
        LogRetentionDays = newLogRetention;
        AuditLogRetentionDays = newAuditRetention;

        var details = AuditDiff.Build(
            AuditDiff.Of(LogsRetentionService.RetentionDaysKey, oldLogRetention.ToString(), newLogRetention.ToString()),
            AuditDiff.Of(AuditLogRetentionService.RetentionDaysKey, oldAuditRetention.ToString(), newAuditRetention.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        LogPath = LogPaths.AppLogsDirectory(configuration);
        ConfiguredLogPathOverride = configuration[LogPaths.ConfigKey];
        return Page();
    }
}
