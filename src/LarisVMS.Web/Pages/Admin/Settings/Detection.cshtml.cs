using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin.Settings;

/// <summary>Global AI-detection defaults — the base of the Camera &rarr; Node &rarr; Global chain a
/// per-camera override on Cameras/Edit falls back to for Confidence/IoU/StreamRole. Split into its
/// own tab the same way Recording's own settings were, since none of these previously had any admin
/// UI at all (the Setting rows existed and were already read by NodeService, just never written from
/// anywhere).</summary>
[Authorize("Settings.Edit")]
public class DetectionModel(ISettingsResolver settings, IAuditService auditService) : PageModel
{
    [BindProperty] public double Confidence { get; set; } = 0.35;
    [BindProperty] public double Iou { get; set; } = 0.5;
    [BindProperty] public string StreamRole { get; set; } = "Sub";
    [BindProperty] public bool ReportIdleDetections { get; set; }
    [BindProperty] public int IdleTimeoutSeconds { get; set; } = 10;

    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        Confidence = await settings.GetAsync("Detection.Confidence", 0.35);
        Iou = await settings.GetAsync("Detection.Iou", 0.5);
        StreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub");
        ReportIdleDetections = await settings.GetAsync("Detection.ReportIdleDetections", false);
        IdleTimeoutSeconds = await settings.GetAsync("Detection.IdleTimeoutSeconds", 10);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var by = User.Identity?.Name;

        var oldConfidence = await settings.GetAsync("Detection.Confidence", 0.35);
        var oldIou = await settings.GetAsync("Detection.Iou", 0.5);
        var oldStreamRole = await settings.GetAsync("AiDetection.StreamRole", "Sub");
        var oldReportIdleDetections = await settings.GetAsync("Detection.ReportIdleDetections", false);
        var oldIdleTimeoutSeconds = await settings.GetAsync("Detection.IdleTimeoutSeconds", 10);

        // Confidence/IoU are genuinely 0-1 fractions everywhere downstream (YOLO-family thresholds) —
        // clamped here so a stray out-of-range value typed into the form can't reach NodeService/the
        // Vision Service pipeline unchecked.
        Confidence = Math.Clamp(Confidence, 0, 1);
        Iou = Math.Clamp(Iou, 0, 1);
        IdleTimeoutSeconds = Math.Max(0, IdleTimeoutSeconds);

        await settings.SetGlobalAsync("Detection.Confidence", Confidence.ToString("0.####"), by);
        await settings.SetGlobalAsync("Detection.Iou", Iou.ToString("0.####"), by);
        await settings.SetGlobalAsync("AiDetection.StreamRole", StreamRole, by);
        await settings.SetGlobalAsync("Detection.ReportIdleDetections", ReportIdleDetections.ToString(), by);
        await settings.SetGlobalAsync("Detection.IdleTimeoutSeconds", IdleTimeoutSeconds.ToString(), by);

        var details = AuditDiff.Build(
            AuditDiff.Of("Detection.Confidence", oldConfidence.ToString("0.####"), Confidence.ToString("0.####")),
            AuditDiff.Of("Detection.Iou", oldIou.ToString("0.####"), Iou.ToString("0.####")),
            AuditDiff.Of("AiDetection.StreamRole", oldStreamRole, StreamRole),
            AuditDiff.Of("Detection.ReportIdleDetections", oldReportIdleDetections.ToString(), ReportIdleDetections.ToString()),
            AuditDiff.Of("Detection.IdleTimeoutSeconds", oldIdleTimeoutSeconds.ToString(), IdleTimeoutSeconds.ToString()));

        await auditService.LogAsync("Settings.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, by,
            HttpContext.Connection.RemoteIpAddress?.ToString(), details);

        SavedMessage = "Saved.";
        return Page();
    }
}
