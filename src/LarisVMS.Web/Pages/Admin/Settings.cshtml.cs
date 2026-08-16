using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>
/// Every setting read through <see cref="ISettingsResolver"/> that has no dedicated admin page of
/// its own, edited at Global scope in one place — the base of the Camera → Node → Global chain every
/// per-camera/per-node override on Cameras/Edit and Admin/Nodes ultimately falls back to. Replaces
/// the earlier Retention-only page (folded in here, not duplicated) now that Recording's M8 defaults
/// have the same gap Retention briefly had: overridable per camera, but with nowhere to see or change
/// the base value they're overriding.
/// </summary>
[Authorize("Settings.Edit")]
public class SettingsModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public int RetentionDays { get; set; } = 30;
    [BindProperty] public int WatermarkPercent { get; set; } = 90;
    [BindProperty] public string? StorageRootPath { get; set; }
    [BindProperty] public string RecordingMode { get; set; } = "Continuous";
    [BindProperty] public int MotionPreRollSeconds { get; set; } = 10;
    [BindProperty] public int MotionPostRollSeconds { get; set; } = 30;
    [BindProperty] public string? RegistrationKey { get; set; }

    /// <summary>Global-only gate (no per-node override, unlike Retention/Recording.Mode above) for
    /// whether a checking-in node is ever offered an update at all — see Program.cs's heartbeat
    /// handler. Defaults on: once a build is uploaded on
    /// <a href="/Admin/NodeBuilds/Index">Admin -&gt; Node Builds</a>, nodes should pick it up without
    /// an extra step here unless an admin deliberately wants to freeze fleet versions.</summary>
    [BindProperty] public bool NodeAutoUpdateEnabled { get; set; } = true;

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
        NodeAutoUpdateEnabled = await settings.GetAsync("NodeAutoUpdate.Enabled", true);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;

        // Read every current value *before* writing, so the audit entry can report what actually
        // changed rather than just "a save happened" (which is all this logged before). Node.
        // RegistrationKey is diffed as a secret — the entry records that it changed, never the key
        // itself, since the audit log is readable by anyone with Logs.View.
        var oldRetentionDays = await settings.GetAsync("Retention.Days", 30);
        var oldWatermarkPercent = await settings.GetAsync("Storage.WatermarkPercent", 90);
        var oldRecordingMode = await settings.GetAsync("Recording.Mode", "Continuous");
        var oldPreRoll = await settings.GetAsync("Recording.MotionPreRollSeconds", 10);
        var oldPostRoll = await settings.GetAsync("Recording.MotionPostRollSeconds", 30);
        var oldStorageRootPath = await settings.GetRawAsync("Storage.RootPath");
        var oldRegistrationKey = await settings.GetRawAsync("Node.RegistrationKey");
        var oldAutoUpdate = await settings.GetAsync("NodeAutoUpdate.Enabled", true);

        await settings.SetGlobalAsync("Retention.Days", RetentionDays.ToString(), by);
        await settings.SetGlobalAsync("Storage.WatermarkPercent", WatermarkPercent.ToString(), by);
        await settings.SetGlobalAsync("Recording.Mode", RecordingMode, by);
        await settings.SetGlobalAsync("Recording.MotionPreRollSeconds", MotionPreRollSeconds.ToString(), by);
        await settings.SetGlobalAsync("Recording.MotionPostRollSeconds", MotionPostRollSeconds.ToString(), by);
        if (!string.IsNullOrWhiteSpace(StorageRootPath))
            await settings.SetGlobalAsync("Storage.RootPath", StorageRootPath, by);
        if (!string.IsNullOrWhiteSpace(RegistrationKey))
            await settings.SetGlobalAsync("Node.RegistrationKey", RegistrationKey, by);
        await settings.SetGlobalAsync("NodeAutoUpdate.Enabled", NodeAutoUpdateEnabled.ToString(), by);

        // Blank means "leave unchanged" for these two (see the guarded writes above), so they're
        // diffed against themselves in that case rather than reported as cleared.
        var details = AuditDiff.Build(
            AuditDiff.Of("Retention.Days", oldRetentionDays.ToString(), RetentionDays.ToString()),
            AuditDiff.Of("Storage.WatermarkPercent", oldWatermarkPercent.ToString(), WatermarkPercent.ToString()),
            AuditDiff.Of("Recording.Mode", oldRecordingMode, RecordingMode),
            AuditDiff.Of("Recording.MotionPreRollSeconds", oldPreRoll.ToString(), MotionPreRollSeconds.ToString()),
            AuditDiff.Of("Recording.MotionPostRollSeconds", oldPostRoll.ToString(), MotionPostRollSeconds.ToString()),
            AuditDiff.Of("Storage.RootPath", oldStorageRootPath,
                string.IsNullOrWhiteSpace(StorageRootPath) ? oldStorageRootPath : StorageRootPath),
            AuditDiff.Secret("Node.RegistrationKey", oldRegistrationKey,
                string.IsNullOrWhiteSpace(RegistrationKey) ? oldRegistrationKey : RegistrationKey),
            AuditDiff.Of("NodeAutoUpdate.Enabled", oldAutoUpdate.ToString(), NodeAutoUpdateEnabled.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
