using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Camera-related global settings — currently just the daily automatic re-probe
/// (CameraReprobeService). Split out of the old single Admin/Settings page into its own tab.</summary>
[Authorize("Settings.Edit")]
public class CamerasModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public bool DailyReprobeEnabled { get; set; } = CameraReprobeService.DefaultEnabled;
    [BindProperty] public string DailyReprobeAtLocalTime { get; set; } = CameraReprobeService.DefaultAtLocalTime;

    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        DailyReprobeEnabled = await settings.GetAsync(CameraReprobeService.EnabledKey, CameraReprobeService.DefaultEnabled);
        DailyReprobeAtLocalTime = CameraReprobeService
            .ParseTimeOfDay(await settings.GetRawAsync(CameraReprobeService.AtLocalTimeKey))
            .ToString("HH:mm");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;

        var oldEnabled = await settings.GetAsync(CameraReprobeService.EnabledKey, CameraReprobeService.DefaultEnabled);
        var oldAt = CameraReprobeService
            .ParseTimeOfDay(await settings.GetRawAsync(CameraReprobeService.AtLocalTimeKey)).ToString("HH:mm");

        // Normalized before storing, not just when reading: a value that never reaches the database
        // can't be served to a browser by some other code path that forgets to normalize.
        var reprobeAt = CameraReprobeService.ParseTimeOfDay(DailyReprobeAtLocalTime).ToString("HH:mm");
        await settings.SetGlobalAsync(CameraReprobeService.EnabledKey, DailyReprobeEnabled.ToString(), by);
        await settings.SetGlobalAsync(CameraReprobeService.AtLocalTimeKey, reprobeAt, by);
        DailyReprobeAtLocalTime = reprobeAt;

        var details = AuditDiff.Build(
            AuditDiff.Of(CameraReprobeService.EnabledKey, oldEnabled.ToString(), DailyReprobeEnabled.ToString()),
            AuditDiff.Of(CameraReprobeService.AtLocalTimeKey, oldAt, reprobeAt));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
