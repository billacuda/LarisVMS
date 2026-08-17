using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Admin;

/// <summary>
/// The colours every timeline and event badge draws with. Same Settings.Edit gate and same
/// ISettingsResolver-backed storage as Admin/Branding.
///
/// Leaving a field blank stores nothing rather than storing the default, so an untouched deployment
/// keeps tracking the built-in palette — including any later change to it — instead of freezing
/// today's values into the database the first time someone opens this page and saves.
/// </summary>
[Authorize("Settings.Edit")]
public class EventColorsModel(IEventColorService eventColors, IAuditService auditService) : PageModel
{
    [BindProperty] public string? MotionColor { get; set; }
    [BindProperty] public string? RecordingColor { get; set; }

    /// <summary>Per-class colours, keyed by the enum name so the form posts back something readable
    /// and order-independent rather than relying on index positions.</summary>
    [BindProperty] public Dictionary<string, string?> DetectionColors { get; set; } = [];

    public string? SavedMessage { get; set; }
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<DetectionKind> Kinds => DetectionDisplay.AllKinds;

    public static string DefaultFor(DetectionKind kind) => DetectionDisplay.ColorHex(kind);
    public static string Label(DetectionKind kind) => DetectionDisplay.Label(kind);
    public static string Emoji(DetectionKind kind) => DetectionDisplay.Emoji(kind);

    public string DefaultMotion => EventColors.DefaultMotion;
    public string DefaultRecording => EventColors.DefaultRecording;
    public string MotionEmoji => EventColors.MotionEmoji;

    public async Task OnGetAsync()
    {
        var current = await eventColors.GetAsync();
        MotionColor = current.Motion;
        RecordingColor = current.Recording;
        DetectionColors = DetectionDisplay.AllKinds.ToDictionary(
            k => k.ToString(),
            k => current.Detections.TryGetValue(k, out var hex) ? hex : null);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Validated here as well as in the service so the form can name the field it rejected rather
        // than silently dropping it back to the default on save.
        var invalid = new List<string>();
        if (!string.IsNullOrWhiteSpace(MotionColor) && EventColors.Normalize(MotionColor) is null) invalid.Add("Motion");
        if (!string.IsNullOrWhiteSpace(RecordingColor) && EventColors.Normalize(RecordingColor) is null) invalid.Add("Recording");
        foreach (var kind in DetectionDisplay.AllKinds)
        {
            DetectionColors.TryGetValue(kind.ToString(), out var hex);
            if (!string.IsNullOrWhiteSpace(hex) && EventColors.Normalize(hex) is null)
                invalid.Add(DetectionDisplay.Label(kind));
        }

        if (invalid.Count > 0)
        {
            ErrorMessage = "These must be hex values like #28e070: " + string.Join(", ", invalid) + ".";
            return Page();
        }

        var before = await eventColors.GetAsync();

        var detections = new Dictionary<DetectionKind, string>();
        foreach (var kind in DetectionDisplay.AllKinds)
        {
            DetectionColors.TryGetValue(kind.ToString(), out var hex);
            var normalized = EventColors.Normalize(hex);
            if (normalized is not null) detections[kind] = normalized;
        }

        var updated = new EventPalette(
            EventColors.Normalize(MotionColor), EventColors.Normalize(RecordingColor), detections);
        await eventColors.SaveAsync(updated, User.Identity?.Name);

        var fields = new List<AuditDiff.Field>
        {
            AuditDiff.Of("Motion", before.Motion ?? "(default)", updated.Motion ?? "(default)"),
            AuditDiff.Of("Recording", before.Recording ?? "(default)", updated.Recording ?? "(default)")
        };
        foreach (var kind in DetectionDisplay.AllKinds)
        {
            before.Detections.TryGetValue(kind, out var oldHex);
            updated.Detections.TryGetValue(kind, out var newHex);
            fields.Add(AuditDiff.Of(DetectionDisplay.Label(kind), oldHex ?? "(default)", newHex ?? "(default)"));
        }

        await auditService.LogAsync("EventColors.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(),
            AuditDiff.Build([.. fields]));

        SavedMessage = "Event colors saved.";
        await OnGetAsync();
        return Page();
    }
}
