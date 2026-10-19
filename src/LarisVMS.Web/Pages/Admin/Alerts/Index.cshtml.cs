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
    ISettingsResolver settings, DetectionAlertService detectionAlerts, IPermissionService permissions) : PageModel
{
    public record RuleRow(Guid Id, string Name, bool IsEnabled, string ConditionSummary,
        DateTime? LastFiredAt, IReadOnlyList<string> ChannelLabels,
        int Subscribers, bool AllowSubscribe, bool TypeTurnedOff, string? OwnerName)
    {
        /// <summary>The rule's delivery channels as one line, or "(none)".</summary>
        public string ChannelList => ChannelLabels.Count == 0 ? "(none)" : string.Join(", ", ChannelLabels);
    }

    public List<RuleRow> Rules { get; set; } = [];
    public string? ErrorMessage { get; set; }

    /// <summary>?personal=true: users' own notifications, for a Super Admin's oversight. Shared rules
    /// otherwise.</summary>
    [BindProperty(SupportsGet = true)] public bool Personal { get; set; }
    public bool IsSuperAdmin { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        IsSuperAdmin = (await permissions.GetGrantedAsync(User, ct)).IsAdministrator;
        if (Personal && !IsSuperAdmin) return RedirectToPage();

        var rules = await db.AlertRules.Include(r => r.Deliveries).Include(r => r.Subscriptions)
            .Where(r => Personal ? r.OwnerUserId != null : r.OwnerUserId == null)
            .OrderBy(r => r.Name).ToListAsync(ct);
        var ownerIds = rules.Where(r => r.OwnerUserId != null).Select(r => r.OwnerUserId!).Distinct().ToList();
        var ownerNames = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName ?? u.Id, ct);
        var typesOff = new HashSet<AlertConditionType>();
        foreach (var (type, _) in NotificationSettings.Types)
            if (!await NotificationSettings.IsTypeEnabledAsync(settings, type, ct)) typesOff.Add(type);
        var cameraNames = (await cameraService.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
        var nodeNames = (await nodeService.ListAsync(ct)).ToDictionary(n => n.Id, n => n.Name);
        var speedUnit = await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct);

        Rules = rules.Select(r => new RuleRow(
            r.Id, r.Name, r.IsEnabled,
            Summarize(r, cameraNames, nodeNames, speedUnit),
            r.LastFiredAt,
            r.Deliveries.Where(d => d.IsEnabled).Select(d => d.Channel.ToString()).OrderBy(s => s).ToList(),
            r.Subscriptions.Count, r.AllowSubscribe, typesOff.Contains(r.ConditionType),
            r.OwnerUserId is { } owner ? ownerNames.GetValueOrDefault(owner, "(deleted user)") : null
        )).ToList();
        return Page();
    }

    /// <summary>A shared rule, or — for a Super Admin — a user's personal one.</summary>
    private async Task<Core.Entities.AlertRule?> FindManageableAsync(Guid id, CancellationToken ct)
    {
        var rule = await db.AlertRules.FindAsync([id], ct);
        if (rule?.OwnerUserId is not null && !(await permissions.GetGrantedAsync(User, ct)).IsAdministrator) return null;
        return rule;
    }

    public async Task<IActionResult> OnPostToggleEnabledAsync(Guid id, CancellationToken ct)
    {
        var rule = await FindManageableAsync(id, ct);
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
        return RedirectToPage(new { Personal });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        var rule = await FindManageableAsync(id, ct);
        if (rule is not null)
        {
            db.AlertRules.Remove(rule); // cascades to Deliveries and Subscriptions
            await db.SaveChangesAsync(ct);
            detectionAlerts.InvalidateRules();
            await auditService.LogAsync("AlertRule.Delete",
                User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
                HttpContext.Connection.RemoteIpAddress?.ToString(), rule.Name, ct);
        }
        return RedirectToPage(new { Personal });
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
                $"{DetectionWhat(rule)} detected on {DetectionWhere(rule, cameraNames, nodeNames)}{DetectionWhen(rule)}",
            AlertConditionType.MotionDetected =>
                $"Motion on {DetectionWhere(rule, cameraNames, nodeNames)}{DetectionWhen(rule)}",
            AlertConditionType.SpeedOver =>
                $"{DetectionWhat(rule)} at {SpeedUnits.Format(rule.SpeedThresholdKph ?? 0, speedUnit)} or faster on {DetectionWhere(rule, cameraNames, nodeNames)}{DetectionWhen(rule)}",
            AlertConditionType.EvidenceLockExpiringSoon => $"Evidence lock ending soon on {DetectionWhere(rule, cameraNames, nodeNames)}",
            AlertConditionType.EvidenceLockRemoved => $"Evidence lock removed on {DetectionWhere(rule, cameraNames, nodeNames)}",
            _ => rule.ConditionType.ToString()
        };

    private static string DetectionWhat(Core.Entities.AlertRule rule)
    {
        var what = rule.LabelFilter ?? rule.CategoryFilter ?? "Anything";
        what = char.ToUpperInvariant(what[0]) + what[1..];
        return rule.ColorFilter is { } color ? $"{what} ({color})" : what;
    }

    private static string DetectionWhere(Core.Entities.AlertRule rule, Dictionary<Guid, string> cameraNames, Dictionary<Guid, string> nodeNames)
    {
        string where;
        if (rule.CameraId is { } cid) where = $"camera \"{cameraNames.GetValueOrDefault(cid, "(deleted camera)")}\"";
        else if (rule.NodeId is { } nid) where = $"cameras on node \"{nodeNames.GetValueOrDefault(nid, "(deleted node)")}\"";
        else where = "any camera";
        return where + (rule.ZoneId is not null ? ", in a zone" : "");
    }

    private static string DetectionWhen(Core.Entities.AlertRule rule)
    {
        var parts = new List<string>();
        if (rule.ScheduleDays is { } days) parts.Add(days.ToString());
        if (rule.ScheduleStart is { } s && rule.ScheduleEnd is { } e) parts.Add($"{s:HH\\:mm}-{e:HH\\:mm}");
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }
}
