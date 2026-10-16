using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Alerts;

[Authorize("Alerts.Edit")]
public class IndexModel(ApplicationDbContext db, ICameraService cameraService, INodeService nodeService, IAuditService auditService,
    ISettingsResolver settings, DetectionAlertService detectionAlerts) : PageModel
{
    public record RuleRow(Guid Id, string Name, bool IsEnabled, string ConditionSummary,
        DateTime? LastFiredAt, IReadOnlyList<string> ChannelLabels);

    public List<RuleRow> Rules { get; set; } = [];
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var rules = await db.AlertRules.Include(r => r.Deliveries).OrderBy(r => r.Name).ToListAsync(ct);
        var cameraNames = (await cameraService.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var nodeNames = (await nodeService.ListAsync(ct)).ToDictionary(n => n.Id, n => n.Name);
        var speedUnit = await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct);

        Rules = rules.Select(r => new RuleRow(
            r.Id, r.Name, r.IsEnabled,
            Summarize(r, cameraNames, nodeNames, speedUnit),
            r.LastFiredAt,
            r.Deliveries.Where(d => d.IsEnabled).Select(d => d.Channel.ToString()).OrderBy(s => s).ToList()
        )).ToList();
    }

    public async Task<IActionResult> OnPostToggleEnabledAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.AlertRules.FindAsync([id], ct);
        if (rule is not null)
        {
            rule.IsEnabled = !rule.IsEnabled;
            rule.LastModifiedAt = DateTime.UtcNow;
            rule.LastModifiedBy = User.Identity?.Name;
            await db.SaveChangesAsync(ct);
            detectionAlerts.InvalidateRules();
            await auditService.LogAsync(rule.IsEnabled ? "AlertRule.Enable" : "AlertRule.Disable",
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
                HttpContext.Connection.RemoteIpAddress?.ToString(), rule.Name, ct);
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.AlertRules.FindAsync([id], ct);
        if (rule is not null)
        {
            db.AlertRules.Remove(rule); // cascades to Deliveries
            await db.SaveChangesAsync(ct);
            detectionAlerts.InvalidateRules();
            await auditService.LogAsync("AlertRule.Delete",
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
                HttpContext.Connection.RemoteIpAddress?.ToString(), rule.Name, ct);
        }
        return RedirectToPage();
    }

    private static string Summarize(Core.Entities.AlertRule rule, Dictionary<Guid, string> cameraNames, Dictionary<Guid, string> nodeNames, string speedUnit) =>
        rule.ConditionType switch
        {
            AlertConditionType.CameraNotReporting =>
                $"Camera \"{(rule.CameraId is { } cid ? cameraNames.GetValueOrDefault(cid, "(deleted camera)") : "?")}\" not reporting",
            AlertConditionType.NodeOffline =>
                $"Node \"{(rule.NodeId is { } nid ? nodeNames.GetValueOrDefault(nid, "(deleted node)") : "?")}\" offline",
            AlertConditionType.NodeStorageLow =>
                $"Node \"{(rule.NodeId is { } snid ? nodeNames.GetValueOrDefault(snid, "(deleted node)") : "?")}\" storage below {rule.ThresholdPercent}%",
            AlertConditionType.NodeFailoverActivated =>
                $"Node \"{(rule.NodeId is { } fnid ? nodeNames.GetValueOrDefault(fnid, "(deleted node)") : "?")}\" failover activated",
            AlertConditionType.ObjectDetected =>
                $"{DetectionWhat(rule)} detected on {DetectionWhere(rule, cameraNames)}{DetectionWhen(rule)}",
            AlertConditionType.SpeedOver =>
                $"{DetectionWhat(rule)} at {SpeedUnits.Format(rule.SpeedThresholdKph ?? 0, speedUnit)} or faster on {DetectionWhere(rule, cameraNames)}{DetectionWhen(rule)}",
            _ => rule.ConditionType.ToString()
        };

    private static string DetectionWhat(Core.Entities.AlertRule rule)
    {
        var what = rule.LabelFilter ?? rule.CategoryFilter ?? "Anything";
        what = char.ToUpperInvariant(what[0]) + what[1..];
        return rule.ColorFilter is { } color ? $"{what} ({color})" : what;
    }

    private static string DetectionWhere(Core.Entities.AlertRule rule, Dictionary<Guid, string> cameraNames) =>
        (rule.CameraId is { } cid ? $"camera \"{cameraNames.GetValueOrDefault(cid, "(deleted camera)")}\"" : "any camera")
        + (rule.ZoneId is not null ? ", in a zone" : "");

    private static string DetectionWhen(Core.Entities.AlertRule rule)
    {
        var parts = new List<string>();
        if (rule.ScheduleDays is { } days) parts.Add(days.ToString());
        if (rule.ScheduleStart is { } s && rule.ScheduleEnd is { } e) parts.Add($"{s:HH\\:mm}-{e:HH\\:mm}");
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }
}
