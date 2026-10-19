using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Alerts;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Web.Pages.Admin.Alerts;

/// <summary>
/// Create/edit one AlertRule and its Deliveries. Deliberately at most one AlertDelivery per
/// AlertChannel per rule — the form renders exactly six fixed rows (one per channel) rather than a
/// dynamically add/removable list, since wanting e.g. two different webhook URLs on the same rule is
/// an edge case not worth the client-side array-binding complexity here. Delivery config fields are
/// shown decrypted on load and overwritten whole on save (unlike EmailSettings' passwords, which mask
/// on redisplay) — these are integration endpoints an admin with Alerts.Edit needs to review, not
/// login credentials rotated often enough that "blank means unchanged" pulls its weight.
/// </summary>
[Authorize("Alerts.Edit")]
public class EditModel(ApplicationDbContext db, ICameraService cameraService, INodeService nodeService, IAuditService auditService,
    ITimelineService timelineService, ISettingsResolver settings, DetectionAlertService detectionAlerts) : PageModel
{
    [BindProperty] public Guid? Id { get; set; }
    [BindProperty] public string Name { get; set; } = string.Empty;
    [BindProperty] public bool IsEnabled { get; set; } = true;
    [BindProperty] public AlertConditionType ConditionType { get; set; } = AlertConditionType.CameraNotReporting;
    [BindProperty] public Guid? CameraId { get; set; }
    [BindProperty] public Guid? NodeId { get; set; }
    [BindProperty] public int ThresholdPercent { get; set; } = 10;
    [BindProperty] public int CooldownMinutes { get; set; } = 60;

    // Detection filters (ObjectDetected / SpeedOver).
    [BindProperty] public string? CategoryFilter { get; set; }
    [BindProperty] public string? LabelFilter { get; set; }
    [BindProperty] public string? ColorFilter { get; set; }
    [BindProperty] public Guid? ZoneId { get; set; }
    /// <summary>In the display unit (Detection.SpeedUnits); stored as km/h.</summary>
    [BindProperty] public double SpeedThreshold { get; set; } = 50;
    [BindProperty] public List<DayOfWeek> ScheduleDays { get; set; } = [];
    [BindProperty] public TimeOnly? ScheduleStart { get; set; }
    [BindProperty] public TimeOnly? ScheduleEnd { get; set; }

    /// <summary>The browser's own time zone, filled in by the page's script on save — the schedule is
    /// evaluated in it.</summary>
    [BindProperty] public string? TimeZoneId { get; set; }

    /// <summary>Users may subscribe to this rule themselves on My notifications.</summary>
    [BindProperty] public bool AllowSubscribe { get; set; }

    [BindProperty] public bool EmailEnabled { get; set; }
    [BindProperty] public string? EmailTo { get; set; }

    [BindProperty] public bool WebhookEnabled { get; set; }
    [BindProperty] public string? WebhookUrl { get; set; }

    [BindProperty] public bool NtfyEnabled { get; set; }
    [BindProperty] public string? NtfyTopic { get; set; }
    [BindProperty] public string? NtfyServerUrl { get; set; }

    [BindProperty] public bool PushoverEnabled { get; set; }
    [BindProperty] public string? PushoverAppToken { get; set; }
    [BindProperty] public string? PushoverUserKey { get; set; }

    [BindProperty] public bool SlackEnabled { get; set; }
    [BindProperty] public string? SlackWebhookUrl { get; set; }

    [BindProperty] public bool TeamsEnabled { get; set; }
    [BindProperty] public string? TeamsWebhookUrl { get; set; }

    public List<Camera> Cameras { get; set; } = [];
    public List<Node> Nodes { get; set; } = [];
    public List<DetectedObjectCategoryDto> Categories { get; set; } = [];
    public List<DetectedObjectLabelDto> Labels { get; set; } = [];
    public List<(Guid Id, Guid CameraId, string Name, ZoneKind Kind)> Zones { get; set; } = [];
    public string SpeedUnit { get; set; } = SpeedUnits.Default;
    public string? ErrorMessage { get; set; }

    public static bool IsDetection(AlertConditionType t) => AlertRuleEditing.IsDetection(t);

    /// <summary>Who gets this rule as a personal notification.</summary>
    public sealed record SubscriberRow(Guid Id, string UserName, bool Browser, bool Email, DateTime? MutedUntilUtc);
    public List<SubscriberRow> Subscribers { get; set; } = [];
    public List<(string Id, string Name)> Users { get; set; } = [];
    public List<(string Id, string Name)> Roles { get; set; } = [];

    /// <summary>The zone a saved schedule is read in, for the hint under the schedule.</summary>
    public string? ScheduleTimeZoneName { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        await LoadPickersAsync(ct);
        if (id is null) return Page();

        var rule = await db.AlertRules.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id, ct);
        // Personal notifications belong to their owner and are edited on My notifications.
        if (rule is null || rule.OwnerUserId is not null) return RedirectToPage("Index");

        Id = rule.Id;
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
        ScheduleTimeZoneName = DetectionAlertMatcher.ResolveTimeZone(rule.TimeZoneId)?.DisplayName;
        AllowSubscribe = rule.AllowSubscribe;

        foreach (var delivery in rule.Deliveries) LoadDelivery(delivery);
        await LoadSubscribersAsync(rule.Id, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        var input = new AlertRuleInput(Name ?? "", IsEnabled, ConditionType, CameraId, NodeId, ThresholdPercent, CooldownMinutes,
            CategoryFilter, LabelFilter, ColorFilter, ZoneId, SpeedThreshold, ScheduleDays, ScheduleStart, ScheduleEnd, TimeZoneId);
        var error = await AlertRuleEditing.ValidateAsync(db, input, ct);
        if (error is not null)
        {
            ErrorMessage = error;
            await LoadPickersAsync(ct);
            if (Id is { } failedId) await LoadSubscribersAsync(failedId, ct);
            return Page();
        }

        AlertRule rule;
        if (Id is { } id)
        {
            var existing = await db.AlertRules.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id && r.OwnerUserId == null, ct);
            if (existing is null)
            {
                ErrorMessage = "That rule no longer exists.";
                await LoadPickersAsync(ct);
                return Page();
            }
            rule = existing;
        }
        else
        {
            rule = new AlertRule { Id = Guid.NewGuid() };
            db.AlertRules.Add(rule);
        }

        await AlertRuleEditing.ApplyAsync(rule, input, settings, User.Identity?.Name, ct);
        rule.AllowSubscribe = AllowSubscribe;

        ReconcileDeliveries(rule);

        await db.SaveChangesAsync(ct);
        await auditService.LogAsync(Id is null ? "AlertRule.Create" : "AlertRule.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(), rule.Name, ct);
        detectionAlerts.InvalidateRules();

        // A new rule stays open so subscribers can be added straight away.
        return Id is null ? RedirectToPage("Edit", new { id = rule.Id }) : RedirectToPage("Index");
    }

    // ── Subscribers ──────────────────────────────────────────────────────

    /// <summary>Adds a user, or every member of a role, as subscribers. Members added through a role
    /// become individual subscriptions, so each can later opt out on their own.</summary>
    public async Task<IActionResult> OnPostAddSubscribersAsync(Guid id, string? addUserId, string? addRoleId, bool addBrowser, bool addEmail, CancellationToken ct)
    {
        var rule = await db.AlertRules.FirstOrDefaultAsync(r => r.Id == id && r.OwnerUserId == null, ct);
        if (rule is null) return RedirectToPage("Index");

        var userIds = new List<string>();
        if (!string.IsNullOrEmpty(addUserId)) userIds.Add(addUserId);
        if (!string.IsNullOrEmpty(addRoleId))
            userIds.AddRange(await db.UserRoles.Where(ur => ur.RoleId == addRoleId).Select(ur => ur.UserId).ToListAsync(ct));
        var existing = await db.AlertSubscriptions.Where(s => s.AlertRuleId == id).Select(s => s.UserId).ToListAsync(ct);
        var toAdd = userIds.Distinct().Except(existing).ToList();
        foreach (var userId in toAdd)
            db.AlertSubscriptions.Add(new AlertSubscription
            {
                Id = Guid.NewGuid(), AlertRuleId = id, UserId = userId, Browser = addBrowser || !addEmail, Email = addEmail,
                CreatedBy = User.Identity?.Name,
            });
        await db.SaveChangesAsync(ct);

        if (toAdd.Count > 0)
        {
            var names = await db.Users.Where(u => toAdd.Contains(u.Id)).Select(u => u.UserName).ToListAsync(ct);
            await auditService.LogAsync("AlertRule.SubscribersAdded", User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), $"{rule.Name}: {string.Join(", ", names)}", ct);
        }
        return RedirectToPage("Edit", new { id });
    }

    public async Task<IActionResult> OnPostRemoveSubscriberAsync(Guid id, Guid subscriptionId, CancellationToken ct)
    {
        var sub = await db.AlertSubscriptions.Include(s => s.AlertRule)
            .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.AlertRuleId == id && s.AlertRule.OwnerUserId == null, ct);
        if (sub is not null)
        {
            var userName = await db.Users.Where(u => u.Id == sub.UserId).Select(u => u.UserName).FirstOrDefaultAsync(ct);
            db.AlertSubscriptions.Remove(sub);
            await db.SaveChangesAsync(ct);
            await auditService.LogAsync("AlertRule.SubscriberRemoved", User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                User.Identity?.Name, HttpContext.Connection.RemoteIpAddress?.ToString(), $"{sub.AlertRule.Name}: {userName ?? sub.UserId}", ct);
        }
        return RedirectToPage("Edit", new { id });
    }

    private async Task LoadSubscribersAsync(Guid ruleId, CancellationToken ct)
    {
        var subs = await db.AlertSubscriptions.AsNoTracking().Where(s => s.AlertRuleId == ruleId).ToListAsync(ct);
        var ids = subs.Select(s => s.UserId).ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.UserName ?? u.Id, ct);
        Subscribers = subs.Select(s => new SubscriberRow(s.Id, names.GetValueOrDefault(s.UserId, "(deleted user)"), s.Browser, s.Email, s.MutedUntilUtc))
            .OrderBy(s => s.UserName, StringComparer.OrdinalIgnoreCase).ToList();
        Users = (await db.Users.AsNoTracking().OrderBy(u => u.UserName).Select(u => new { u.Id, Name = u.DisplayName ?? u.UserName ?? u.Id }).ToListAsync(ct))
            .Select(u => (u.Id, u.Name)).ToList();
        Roles = (await db.Roles.AsNoTracking().OrderBy(r => r.Name).Select(r => new { r.Id, r.Name }).ToListAsync(ct))
            .Select(r => (r.Id, r.Name ?? r.Id)).ToList();
    }

    /// <summary>Adds/updates/removes each of the six fixed delivery rows to match the six
    /// Enabled/config bound-property groups above — an *Enabled checkbox turned off removes that
    /// channel's row entirely rather than leaving a disabled one behind, since there's nothing else
    /// on this page for a stray disabled row to mean.</summary>
    private void ReconcileDeliveries(AlertRule rule)
    {
        Set(AlertChannel.Email, EmailEnabled, EmailEnabled ? JsonSerializer.Serialize(new EmailDeliveryConfig(EmailTo?.Trim() ?? string.Empty)) : null);
        Set(AlertChannel.Webhook, WebhookEnabled, WebhookEnabled ? JsonSerializer.Serialize(new WebhookDeliveryConfig(WebhookUrl?.Trim() ?? string.Empty)) : null);
        Set(AlertChannel.Ntfy, NtfyEnabled, NtfyEnabled ? JsonSerializer.Serialize(new NtfyDeliveryConfig(NtfyTopic?.Trim() ?? string.Empty, NullIfBlank(NtfyServerUrl))) : null);
        Set(AlertChannel.Pushover, PushoverEnabled, PushoverEnabled ? JsonSerializer.Serialize(new PushoverDeliveryConfig(PushoverAppToken?.Trim() ?? string.Empty, PushoverUserKey?.Trim() ?? string.Empty)) : null);
        Set(AlertChannel.Slack, SlackEnabled, SlackEnabled ? JsonSerializer.Serialize(new SlackDeliveryConfig(SlackWebhookUrl?.Trim() ?? string.Empty)) : null);
        Set(AlertChannel.Teams, TeamsEnabled, TeamsEnabled ? JsonSerializer.Serialize(new TeamsDeliveryConfig(TeamsWebhookUrl?.Trim() ?? string.Empty)) : null);
        return;

        void Set(AlertChannel channel, bool enabled, string? configJson)
        {
            var existing = rule.Deliveries.FirstOrDefault(d => d.Channel == channel);
            if (!enabled)
            {
                if (existing is not null) rule.Deliveries.Remove(existing);
                return;
            }
            if (existing is not null) { existing.ConfigJson = configJson; return; }
            rule.Deliveries.Add(new AlertDelivery { Id = Guid.NewGuid(), AlertRuleId = rule.Id, Channel = channel, ConfigJson = configJson });
        }
    }

    private void LoadDelivery(AlertDelivery delivery)
    {
        switch (delivery.Channel)
        {
            case AlertChannel.Email:
                EmailEnabled = true;
                EmailTo = Deserialize<EmailDeliveryConfig>(delivery.ConfigJson)?.To;
                break;
            case AlertChannel.Webhook:
                WebhookEnabled = true;
                WebhookUrl = Deserialize<WebhookDeliveryConfig>(delivery.ConfigJson)?.Url;
                break;
            case AlertChannel.Ntfy:
                NtfyEnabled = true;
                var ntfy = Deserialize<NtfyDeliveryConfig>(delivery.ConfigJson);
                NtfyTopic = ntfy?.Topic;
                NtfyServerUrl = ntfy?.ServerUrl;
                break;
            case AlertChannel.Pushover:
                PushoverEnabled = true;
                var pushover = Deserialize<PushoverDeliveryConfig>(delivery.ConfigJson);
                PushoverAppToken = pushover?.AppToken;
                PushoverUserKey = pushover?.UserKey;
                break;
            case AlertChannel.Slack:
                SlackEnabled = true;
                SlackWebhookUrl = Deserialize<SlackDeliveryConfig>(delivery.ConfigJson)?.WebhookUrl;
                break;
            case AlertChannel.Teams:
                TeamsEnabled = true;
                TeamsWebhookUrl = Deserialize<TeamsDeliveryConfig>(delivery.ConfigJson)?.WebhookUrl;
                break;
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json);


    private async Task LoadPickersAsync(CancellationToken ct)
    {
        Cameras = (await cameraService.ListAsync(ct)).OrderBy(c => c.Name).ToList();
        Nodes = (await nodeService.ListAsync(ct)).OrderBy(n => n.Name).ToList();
        Categories = await timelineService.GetDetectedObjectCategoriesAsync(ct);
        Labels = await timelineService.GetDetectedObjectLabelsAsync(ct);
        Zones = (await db.Zones.AsNoTracking().Where(z => z.Kind != ZoneKind.Privacy)
                .OrderBy(z => z.Name).Select(z => new { z.Id, z.CameraId, z.Name, z.Kind }).ToListAsync(ct))
            .Select(z => (z.Id, z.CameraId, z.Name, z.Kind)).ToList();
        SpeedUnit = SpeedUnits.Normalize(await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct));
    }
}
