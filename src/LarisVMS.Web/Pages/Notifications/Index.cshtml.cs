using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Notifications;

/// <summary>
/// My notifications: the signed-in user's own view of personal notifications.
/// <list type="bullet">
/// <item>Available — shared rules an admin opened for subscription (or added this user to): subscribe,
/// choose browser and/or email, mute, unsubscribe. Needs Notifications.Subscribe.</item>
/// <item>Mine — notifications the user built for themselves (personal AlertRules, OwnerUserId = this
/// user), with the same filters as the admin editor, limited to cameras the user can view and to
/// notification types an admin left on. Needs Notifications.CreateOwn.</item>
/// <item>This browser and devices — turning push on per browser (notifications.js) and the device list.</item>
/// </list>
/// Everything here acts on the signed-in user's own rows only.
/// </summary>
[Authorize]
public class IndexModel(ApplicationDbContext db, ICameraService cameraService, INodeService nodeService, ICameraAccessService cameraAccess,
    IPermissionService permissions, ISettingsResolver settings, IEmailService email, ITimelineService timelineService,
    IAuditService auditService, DetectionAlertService detectionAlerts) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "available";
    [BindProperty(SupportsGet = true)] public string? Edit { get; set; }

    public sealed record AvailableRow(Guid RuleId, string Name, string Description, AlertSubscription? Subscription);
    public sealed record MineRow(Guid RuleId, string Name, string Description, bool IsEnabled, bool Browser, bool Email, bool TypeTurnedOff);

    public List<AvailableRow> Available { get; set; } = [];
    public List<MineRow> Mine { get; set; } = [];
    public bool CanSubscribe { get; set; }
    public bool CanCreateOwn { get; set; }
    public bool EmailAvailable { get; set; }
    public string? Message { get; set; }
    public bool MessageIsError { get; set; }

    // ── Personal rule editor ──
    [BindProperty] public Guid? RuleId { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public bool IsEnabled { get; set; } = true;
    [BindProperty] public AlertConditionType ConditionType { get; set; } = AlertConditionType.ObjectDetected;
    [BindProperty] public Guid? CameraId { get; set; }
    [BindProperty] public Guid? NodeId { get; set; }
    [BindProperty] public int ThresholdPercent { get; set; } = 10;
    [BindProperty] public int CooldownMinutes { get; set; } = 15;
    [BindProperty] public string? CategoryFilter { get; set; }
    [BindProperty] public string? LabelFilter { get; set; }
    [BindProperty] public string? ColorFilter { get; set; }
    [BindProperty] public Guid? ZoneId { get; set; }
    [BindProperty] public double SpeedThreshold { get; set; } = 50;
    [BindProperty] public List<DayOfWeek> ScheduleDays { get; set; } = [];
    [BindProperty] public TimeOnly? ScheduleStart { get; set; }
    [BindProperty] public TimeOnly? ScheduleEnd { get; set; }
    [BindProperty] public string? TimeZoneId { get; set; }
    [BindProperty] public bool DeliverBrowser { get; set; } = true;
    [BindProperty] public bool DeliverEmail { get; set; }

    public List<(AlertConditionType Type, string Label)> EnabledTypes { get; set; } = [];
    public List<Camera> Cameras { get; set; } = [];
    public List<Node> Nodes { get; set; } = [];
    public List<DetectedObjectCategoryDto> Categories { get; set; } = [];
    public List<DetectedObjectLabelDto> Labels { get; set; } = [];
    public List<(Guid Id, Guid CameraId, string Name, ZoneKind Kind)> Zones { get; set; } = [];
    public string SpeedUnit { get; set; } = SpeedUnits.Default;

    private string UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        if (Tab == "mine" && Edit is not null && CanCreateOwn) await LoadEditorAsync(ct);
    }

    // ── Available (shared rules) ──────────────────────────────────────────

    public async Task<IActionResult> OnPostSubscribeAsync(Guid ruleId, bool browser, bool email, CancellationToken ct)
    {
        if (!await HasAsync("Notifications", "Subscribe", ct)) return Forbid();
        // Only a rule this page would list for them: its camera or node has to be one they can see.
        await LoadAsync(ct);
        if (Available.All(a => a.RuleId != ruleId)) return RedirectToPage(new { tab = "available" });
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.OwnerUserId == null, ct);
        var sub = await db.AlertSubscriptions.FirstOrDefaultAsync(s => s.AlertRuleId == ruleId && s.UserId == UserId, ct);
        if (rule is null || (!rule.AllowSubscribe && sub is null)) return RedirectToPage(new { tab = "available" });

        if (!browser && !email) browser = true;
        if (sub is null)
        {
            db.AlertSubscriptions.Add(new AlertSubscription
            {
                Id = Guid.NewGuid(), AlertRuleId = ruleId, UserId = UserId, Browser = browser, Email = email, CreatedBy = User.Identity?.Name,
            });
            await LogAsync("Notification.Subscribe", rule.Name, ct);
        }
        else
        {
            sub.Browser = browser;
            sub.Email = email;
        }
        await db.SaveChangesAsync(ct);
        return RedirectToPage(new { tab = "available" });
    }

    public async Task<IActionResult> OnPostUnsubscribeAsync(Guid ruleId, CancellationToken ct)
    {
        var sub = await db.AlertSubscriptions.Include(s => s.AlertRule)
            .FirstOrDefaultAsync(s => s.AlertRuleId == ruleId && s.UserId == UserId && s.AlertRule.OwnerUserId == null, ct);
        if (sub is not null)
        {
            db.AlertSubscriptions.Remove(sub);
            await db.SaveChangesAsync(ct);
            await LogAsync("Notification.Unsubscribe", sub.AlertRule.Name, ct);
        }
        return RedirectToPage(new { tab = "available" });
    }

    /// <summary>muteFor: hours ("1", "8", "24"), "forever" (until turned back on), or "off".</summary>
    public async Task<IActionResult> OnPostMuteAsync(Guid ruleId, string muteFor, string? returnTab, CancellationToken ct)
    {
        var sub = await db.AlertSubscriptions.FirstOrDefaultAsync(s => s.AlertRuleId == ruleId && s.UserId == UserId, ct);
        if (sub is not null)
        {
            sub.MutedUntilUtc = muteFor switch
            {
                "off" => null,
                "forever" => DateTime.MaxValue,
                _ when int.TryParse(muteFor, out var hours) && hours is > 0 and <= 168 => DateTime.UtcNow.AddHours(hours),
                _ => sub.MutedUntilUtc,
            };
            await db.SaveChangesAsync(ct);
        }
        return RedirectToPage(new { tab = returnTab == "mine" ? "mine" : "available" });
    }

    // ── Mine (personal rules) ─────────────────────────────────────────────

    public async Task<IActionResult> OnPostSaveRuleAsync(CancellationToken ct)
    {
        if (!await HasAsync("Notifications", "CreateOwn", ct)) return Forbid();
        await LoadAsync(ct);
        Tab = "mine";
        Edit = RuleId?.ToString() ?? "new";

        var error = ValidateOwnRule();
        if (error is null)
        {
            var input = new AlertRuleInput(Name ?? "", IsEnabled, ConditionType, CameraId, NodeId, ThresholdPercent, CooldownMinutes,
                CategoryFilter, LabelFilter, ColorFilter, ZoneId, SpeedThreshold, ScheduleDays, ScheduleStart, ScheduleEnd, TimeZoneId);
            error = await AlertRuleEditing.ValidateAsync(db, input, ct);
            if (error is null)
            {
                AlertRule? rule;
                if (RuleId is { } id)
                {
                    rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id && r.OwnerUserId == UserId, ct);
                    if (rule is null) return RedirectToPage(new { tab = "mine" });
                }
                else
                {
                    rule = new AlertRule { Id = Guid.NewGuid(), OwnerUserId = UserId };
                    db.AlertRules.Add(rule);
                }
                await AlertRuleEditing.ApplyAsync(rule, input, settings, User.Identity?.Name, ct);
                rule.AllowSubscribe = false;

                // The owner's own subscription carries how they want to receive it.
                var sub = await db.AlertSubscriptions.FirstOrDefaultAsync(s => s.AlertRuleId == rule.Id && s.UserId == UserId, ct);
                if (sub is null)
                {
                    sub = new AlertSubscription { Id = Guid.NewGuid(), AlertRuleId = rule.Id, UserId = UserId, CreatedBy = User.Identity?.Name };
                    db.AlertSubscriptions.Add(sub);
                }
                sub.Browser = DeliverBrowser || !(DeliverEmail && EmailAvailable);
                sub.Email = DeliverEmail && EmailAvailable;

                await db.SaveChangesAsync(ct);
                detectionAlerts.InvalidateRules();
                await LogAsync(RuleId is null ? "Notification.Create" : "Notification.Update", rule.Name, ct);
                return RedirectToPage(new { tab = "mine" });
            }
        }

        Message = error;
        MessageIsError = true;
        await LoadPickersAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleRuleAsync(Guid ruleId, CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.OwnerUserId == UserId, ct);
        if (rule is not null)
        {
            rule.IsEnabled = !rule.IsEnabled;
            rule.LastModifiedAt = DateTime.UtcNow;
            rule.LastModifiedBy = User.Identity?.Name;
            await db.SaveChangesAsync(ct);
            detectionAlerts.InvalidateRules();
        }
        return RedirectToPage(new { tab = "mine" });
    }

    public async Task<IActionResult> OnPostDeleteRuleAsync(Guid ruleId, CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.OwnerUserId == UserId, ct);
        if (rule is not null)
        {
            db.AlertRules.Remove(rule);
            await db.SaveChangesAsync(ct);
            detectionAlerts.InvalidateRules();
            await LogAsync("Notification.Delete", rule.Name, ct);
        }
        return RedirectToPage(new { tab = "mine" });
    }

    /// <summary>The checks the admin editor doesn't need: only switched-on types, only cameras this
    /// user can view, and node-wide types only for users who can see node health.</summary>
    private string? ValidateOwnRule()
    {
        if (EnabledTypes.All(t => t.Type != ConditionType)) return "That kind of notification isn't available.";
        if (CameraId is { } cam && Cameras.All(c => c.Id != cam)) return "You don't have access to that camera.";
        if (NodeId is { } node && Nodes.All(n => n.Id != node)) return "You don't have access to that node.";
        if (DeliverEmail && !EmailAvailable && !DeliverBrowser) return "Email notifications aren't available. Choose the browser.";
        return null;
    }

    // ── Loading ───────────────────────────────────────────────────────────

    private async Task LoadAsync(CancellationToken ct)
    {
        var granted = await permissions.GetGrantedAsync(User, ct);
        CanSubscribe = granted.Has("Notifications", "Subscribe");
        CanCreateOwn = granted.Has("Notifications", "CreateOwn");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == UserId, ct);
        EmailAvailable = !string.IsNullOrWhiteSpace(user?.Email)
            && await settings.GetAsync(NotificationSettings.EmailEnabled, true, ct: ct)
            && await email.IsConfiguredAsync(ct);

        EnabledTypes = [];
        var canSeeNodes = granted.Has("Dashboard", "View") || granted.Has("Nodes", "Edit");
        foreach (var (type, label) in NotificationSettings.Types)
        {
            if (!await NotificationSettings.IsTypeEnabledAsync(settings, type, ct)) continue;
            if (AlertRuleEditing.IsNodeCondition(type) && !canSeeNodes) continue;
            EnabledTypes.Add((type, label));
        }

        // Cameras this user can view; nodes only when they can see node health.
        var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(User, CameraAccessActions.View, ct);
        Cameras = (await cameraService.ListAsync(ct)).Where(c => accessible is null || accessible.Contains(c.Id)).OrderBy(c => c.Name).ToList();
        var cameraNodeIds = Cameras.Where(c => c.NodeId is not null).Select(c => c.NodeId!.Value).ToHashSet();
        Nodes = canSeeNodes ? (await nodeService.ListAsync(ct)).Where(n => accessible is null || cameraNodeIds.Contains(n.Id)).OrderBy(n => n.Name).ToList() : [];
        var cameraNames = Cameras.ToDictionary(c => c.Id, c => c.Name);
        var nodeNames = Nodes.ToDictionary(n => n.Id, n => n.Name);

        var mySubs = await db.AlertSubscriptions.AsNoTracking().Where(s => s.UserId == UserId).ToListAsync(ct);
        var enabledTypeSet = EnabledTypes.Select(t => t.Type).ToHashSet();

        // Shared rules open for subscription, plus any an admin subscribed this user to.
        var subscribedIds = mySubs.Select(s => s.AlertRuleId).ToList();
        var shared = await db.AlertRules.AsNoTracking()
            .Where(r => r.OwnerUserId == null && r.IsEnabled && (r.AllowSubscribe || subscribedIds.Contains(r.Id)))
            .OrderBy(r => r.Name).ToListAsync(ct);
        Available = shared
            .Where(r => enabledTypeSet.Contains(r.ConditionType) || subscribedIds.Contains(r.Id))
            .Where(r => r.CameraId is not { } cam || cameraNames.ContainsKey(cam))
            .Where(r => r.NodeId is not { } node || nodeNames.ContainsKey(node))
            .Where(r => !AlertRuleEditing.IsNodeCondition(r.ConditionType) || canSeeNodes)
            .Select(r => new AvailableRow(r.Id, r.Name, AlertRuleEditing.Describe(r, cameraNames, nodeNames),
                mySubs.FirstOrDefault(s => s.AlertRuleId == r.Id)))
            .ToList();

        var mine = await db.AlertRules.AsNoTracking().Where(r => r.OwnerUserId == UserId).OrderBy(r => r.Name).ToListAsync(ct);
        Mine = mine.Select(r =>
        {
            var sub = mySubs.FirstOrDefault(s => s.AlertRuleId == r.Id);
            return new MineRow(r.Id, r.Name, AlertRuleEditing.Describe(r, cameraNames, nodeNames), r.IsEnabled,
                sub?.Browser ?? false, sub?.Email ?? false, !enabledTypeSet.Contains(r.ConditionType));
        }).ToList();
        MutedUntil = mySubs.Where(s => s.MutedUntilUtc > DateTime.UtcNow).ToDictionary(s => s.AlertRuleId, s => s.MutedUntilUtc!.Value);
    }

    /// <summary>Muted subscriptions by rule, for the Mute control's current state.</summary>
    public Dictionary<Guid, DateTime> MutedUntil { get; set; } = [];

    private async Task LoadEditorAsync(CancellationToken ct)
    {
        await LoadPickersAsync(ct);
        if (!Guid.TryParse(Edit, out var id)) return; // "new"
        var rule = await db.AlertRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.OwnerUserId == UserId, ct);
        if (rule is null) { Edit = null; return; }

        RuleId = rule.Id;
        Name = rule.Name;
        IsEnabled = rule.IsEnabled;
        ConditionType = rule.ConditionType;
        CameraId = rule.CameraId;
        NodeId = rule.NodeId;
        ThresholdPercent = rule.ThresholdPercent ?? 10;
        CooldownMinutes = rule.CooldownMinutes;
        CategoryFilter = rule.CategoryFilter;
        LabelFilter = rule.LabelFilter;
        ColorFilter = rule.ColorFilter;
        ZoneId = rule.ZoneId;
        if (rule.SpeedThresholdKph is { } kph) SpeedThreshold = Math.Round(SpeedUnits.FromKph(kph, SpeedUnit), 1);
        ScheduleDays = AlertRuleEditing.DaysOf(rule.ScheduleDays);
        ScheduleStart = rule.ScheduleStart;
        ScheduleEnd = rule.ScheduleEnd;
        TimeZoneId = rule.TimeZoneId;
        var sub = await db.AlertSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.AlertRuleId == rule.Id && s.UserId == UserId, ct);
        DeliverBrowser = sub?.Browser ?? true;
        DeliverEmail = sub?.Email ?? false;
    }

    private async Task LoadPickersAsync(CancellationToken ct)
    {
        Categories = await timelineService.GetDetectedObjectCategoriesAsync(ct);
        Labels = await timelineService.GetDetectedObjectLabelsAsync(ct);
        var cameraIds = Cameras.Select(c => c.Id).ToList();
        Zones = (await db.Zones.AsNoTracking().Where(z => z.Kind != ZoneKind.Privacy && cameraIds.Contains(z.CameraId))
                .OrderBy(z => z.Name).Select(z => new { z.Id, z.CameraId, z.Name, z.Kind }).ToListAsync(ct))
            .Select(z => (z.Id, z.CameraId, z.Name, z.Kind)).ToList();
        SpeedUnit = SpeedUnits.Normalize(await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct));
    }

    private async Task<bool> HasAsync(string resource, string action, CancellationToken ct)
        => (await permissions.GetGrantedAsync(User, ct)).Has(resource, action);

    private Task LogAsync(string action, string details, CancellationToken ct)
        => auditService.LogAsync(action, UserId, User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), details, ct);
}
