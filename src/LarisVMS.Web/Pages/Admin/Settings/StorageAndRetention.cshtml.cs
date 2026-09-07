using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Global retention/watermark/archive-policy defaults — the base of the Camera &rarr; Node
/// &rarr; Global chain a per-camera or per-node override falls back to. Storage <em>paths</em> are
/// not here: every recorder node sets its own storage path (and optional archive path) on
/// Admin &rarr; Nodes, populated when the node is added.
///
/// Roles/permissions overhaul, pass 3: gated by Retention.Edit (was Settings.Edit) — matches
/// permission_matrix.txt's "Data retention policy management" row.</summary>
[Authorize("Retention.Edit")]
public class StorageAndRetentionModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public int RetentionDays { get; set; } = 30;
    [BindProperty] public int WatermarkPercent { get; set; } = 90;

    [BindProperty] public bool ArchiveEnabled { get; set; }
    [BindProperty] public int ArchiveRetentionDays { get; set; }

    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        RetentionDays = await settings.GetAsync("Retention.Days", 30);
        WatermarkPercent = await settings.GetAsync("Storage.WatermarkPercent", 90);
        ArchiveEnabled = await settings.GetAsync("Archive.Enabled", false);
        ArchiveRetentionDays = await settings.GetAsync("Archive.RetentionDays", 0);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;

        var oldRetentionDays = await settings.GetAsync("Retention.Days", 30);
        var oldWatermarkPercent = await settings.GetAsync("Storage.WatermarkPercent", 90);
        var oldArchiveEnabled = await settings.GetAsync("Archive.Enabled", false);
        var oldArchiveRetentionDays = await settings.GetAsync("Archive.RetentionDays", 0);

        await settings.SetGlobalAsync("Retention.Days", RetentionDays.ToString(), by);
        await settings.SetGlobalAsync("Storage.WatermarkPercent", WatermarkPercent.ToString(), by);
        await settings.SetGlobalAsync("Archive.Enabled", ArchiveEnabled.ToString(), by);
        await settings.SetGlobalAsync("Archive.RetentionDays", ArchiveRetentionDays.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Retention.Days", oldRetentionDays.ToString(), RetentionDays.ToString()),
            AuditDiff.Of("Storage.WatermarkPercent", oldWatermarkPercent.ToString(), WatermarkPercent.ToString()),
            AuditDiff.Of("Archive.Enabled", oldArchiveEnabled.ToString(), ArchiveEnabled.ToString()),
            AuditDiff.Of("Archive.RetentionDays", oldArchiveRetentionDays.ToString(), ArchiveRetentionDays.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
