using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Global storage/retention defaults — the base of the Camera &rarr; Node &rarr; Global
/// chain a per-camera or per-node override falls back to. Split out of the old single
/// Admin/Settings page into its own tab.</summary>
[Authorize("Settings.Edit")]
public class StorageAndRetentionModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public string? StorageRootPath { get; set; }
    [BindProperty] public int RetentionDays { get; set; } = 30;
    [BindProperty] public int WatermarkPercent { get; set; } = 90;

    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        StorageRootPath = await settings.GetRawAsync("Storage.RootPath");
        RetentionDays = await settings.GetAsync("Retention.Days", 30);
        WatermarkPercent = await settings.GetAsync("Storage.WatermarkPercent", 90);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;

        var oldStorageRootPath = await settings.GetRawAsync("Storage.RootPath");
        var oldRetentionDays = await settings.GetAsync("Retention.Days", 30);
        var oldWatermarkPercent = await settings.GetAsync("Storage.WatermarkPercent", 90);

        // Blank means "leave unchanged" — the form always submits the current path, same convention
        // credential fields already use.
        if (!string.IsNullOrWhiteSpace(StorageRootPath))
            await settings.SetGlobalAsync("Storage.RootPath", StorageRootPath, by);
        await settings.SetGlobalAsync("Retention.Days", RetentionDays.ToString(), by);
        await settings.SetGlobalAsync("Storage.WatermarkPercent", WatermarkPercent.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Storage.RootPath", oldStorageRootPath,
                string.IsNullOrWhiteSpace(StorageRootPath) ? oldStorageRootPath : StorageRootPath),
            AuditDiff.Of("Retention.Days", oldRetentionDays.ToString(), RetentionDays.ToString()),
            AuditDiff.Of("Storage.WatermarkPercent", oldWatermarkPercent.ToString(), WatermarkPercent.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
