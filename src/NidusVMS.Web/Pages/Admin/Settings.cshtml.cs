using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Admin;

/// <summary>
/// Every setting read through <see cref="ISettingsResolver"/> that has no dedicated admin page of
/// its own, edited at Global scope in one place — the base of the Camera → Node → Global chain every
/// per-camera/per-node override on Cameras/Edit and Admin/Nodes ultimately falls back to. Replaces
/// the earlier Retention-only page (folded in here, not duplicated) now that Recording's M8 defaults
/// have the same gap Retention briefly had: overridable per camera, but with nowhere to see or change
/// the base value they're overriding.
/// </summary>
[Authorize("Settings.Edit")]
public class SettingsModel(ISettingsResolver settings) : PageModel
{
    [BindProperty] public int RetentionDays { get; set; } = 30;
    [BindProperty] public int WatermarkPercent { get; set; } = 90;
    [BindProperty] public string? StorageRootPath { get; set; }
    [BindProperty] public string RecordingMode { get; set; } = "Continuous";
    [BindProperty] public int MotionPreRollSeconds { get; set; } = 10;
    [BindProperty] public int MotionPostRollSeconds { get; set; } = 30;
    [BindProperty] public string? RegistrationKey { get; set; }
    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        RetentionDays = await settings.GetAsync("Retention.Days", 30);
        WatermarkPercent = await settings.GetAsync("Storage.WatermarkPercent", 90);
        StorageRootPath = await settings.GetRawAsync("Storage.RootPath");
        RecordingMode = await settings.GetAsync("Recording.Mode", "Continuous");
        MotionPreRollSeconds = await settings.GetAsync("Recording.MotionPreRollSeconds", 10);
        MotionPostRollSeconds = await settings.GetAsync("Recording.MotionPostRollSeconds", 30);
        RegistrationKey = await settings.GetRawAsync("Node.RegistrationKey");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;
        await settings.SetGlobalAsync("Retention.Days", RetentionDays.ToString(), by);
        await settings.SetGlobalAsync("Storage.WatermarkPercent", WatermarkPercent.ToString(), by);
        await settings.SetGlobalAsync("Recording.Mode", RecordingMode, by);
        await settings.SetGlobalAsync("Recording.MotionPreRollSeconds", MotionPreRollSeconds.ToString(), by);
        await settings.SetGlobalAsync("Recording.MotionPostRollSeconds", MotionPostRollSeconds.ToString(), by);
        if (!string.IsNullOrWhiteSpace(StorageRootPath))
            await settings.SetGlobalAsync("Storage.RootPath", StorageRootPath, by);
        if (!string.IsNullOrWhiteSpace(RegistrationKey))
            await settings.SetGlobalAsync("Node.RegistrationKey", RegistrationKey, by);

        SavedMessage = "Saved.";
        return Page();
    }
}
