using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Global recording-mode defaults — the base of the Camera &rarr; Node &rarr; Global chain
/// a per-camera override on Cameras/Edit falls back to. Split out of the old single Admin/Settings
/// page into its own tab.</summary>
[Authorize("Settings.Edit")]
public class RecordingModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public string RecordingMode { get; set; } = "Continuous";
    [BindProperty] public int MotionPreRollSeconds { get; set; } = 10;
    [BindProperty] public int MotionPostRollSeconds { get; set; } = 30;
    [BindProperty] public int SegmentSeconds { get; set; } = 60;

    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        RecordingMode = await settings.GetAsync("Recording.Mode", "Continuous");
        MotionPreRollSeconds = await settings.GetAsync("Recording.MotionPreRollSeconds", 10);
        MotionPostRollSeconds = await settings.GetAsync("Recording.MotionPostRollSeconds", 30);
        SegmentSeconds = await settings.GetAsync("Recording.SegmentSeconds", 60);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;

        var oldRecordingMode = await settings.GetAsync("Recording.Mode", "Continuous");
        var oldPreRoll = await settings.GetAsync("Recording.MotionPreRollSeconds", 10);
        var oldPostRoll = await settings.GetAsync("Recording.MotionPostRollSeconds", 30);
        var oldSegmentSeconds = await settings.GetAsync("Recording.SegmentSeconds", 60);

        // Same clamp NodeService.GetConfigAsync applies when resolving this for a node — enforced
        // here too so the admin page's own saved value can never disagree with what a node actually
        // ends up running.
        SegmentSeconds = Math.Clamp(SegmentSeconds, 5, 300);

        await settings.SetGlobalAsync("Recording.Mode", RecordingMode, by);
        await settings.SetGlobalAsync("Recording.MotionPreRollSeconds", MotionPreRollSeconds.ToString(), by);
        await settings.SetGlobalAsync("Recording.MotionPostRollSeconds", MotionPostRollSeconds.ToString(), by);
        await settings.SetGlobalAsync("Recording.SegmentSeconds", SegmentSeconds.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Recording.Mode", oldRecordingMode, RecordingMode),
            AuditDiff.Of("Recording.MotionPreRollSeconds", oldPreRoll.ToString(), MotionPreRollSeconds.ToString()),
            AuditDiff.Of("Recording.MotionPostRollSeconds", oldPostRoll.ToString(), MotionPostRollSeconds.ToString()),
            AuditDiff.Of("Recording.SegmentSeconds", oldSegmentSeconds.ToString(), SegmentSeconds.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
