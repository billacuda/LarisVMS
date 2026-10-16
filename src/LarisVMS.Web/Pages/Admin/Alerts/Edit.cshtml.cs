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
    public List<(Guid Id, Guid CameraId, string Name)> Zones { get; set; } = [];
    public string SpeedUnit { get; set; } = SpeedUnits.Default;
    public string? ErrorMessage { get; set; }

    public static bool IsDetection(AlertConditionType t) => t is AlertConditionType.ObjectDetected or AlertConditionType.SpeedOver;

    public async Task<IActionResult> OnGetAsync(Guid? id, CancellationToken ct)
    {
        await LoadPickersAsync(ct);
        if (id is null) return Page();

        var rule = await db.AlertRules.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return RedirectToPage("Index");

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
        ScheduleDays = rule.ScheduleDays is { } days and not DayOfWeekFlags.None
            ? Enum.GetValues<DayOfWeek>().Where(d => days.HasFlag(DayFlag(d))).ToList()
            : [];
        ScheduleStart = rule.ScheduleStart;
        ScheduleEnd = rule.ScheduleEnd;

        foreach (var delivery in rule.Deliveries) LoadDelivery(delivery);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        Name = Name.Trim();
        if (string.IsNullOrEmpty(Name))
        {
            ErrorMessage = "Enter a name for this rule.";
            await LoadPickersAsync(ct);
            return Page();
        }
        if (CooldownMinutes < 1)
        {
            ErrorMessage = "Cooldown must be at least 1 minute.";
            await LoadPickersAsync(ct);
            return Page();
        }

        var conditionError = ConditionType switch
        {
            AlertConditionType.CameraNotReporting when CameraId is null => "Choose a camera.",
            AlertConditionType.NodeOffline when NodeId is null => "Choose a node.",
            AlertConditionType.NodeFailoverActivated when NodeId is null => "Choose a node.",
            AlertConditionType.NodeStorageLow when NodeId is null => "Choose a node.",
            AlertConditionType.NodeStorageLow when ThresholdPercent is < 1 or > 99 => "Threshold must be between 1 and 99.",
            AlertConditionType.SpeedOver when SpeedThreshold is <= 0 or > 400 => "Enter a speed above 0.",
            _ when IsDetection(ConditionType) && ZoneId is not null && CameraId is null => "A zone filter needs a camera.",
            _ when IsDetection(ConditionType) && (ScheduleStart is null) != (ScheduleEnd is null) => "Enter both a start and an end time, or neither.",
            _ when IsDetection(ConditionType) && !string.IsNullOrWhiteSpace(ColorFilter) && !ObjectColors.IsKnown(ColorFilter) => "Choose a color from the list.",
            _ => (string?)null
        };
        if (conditionError is null && IsDetection(ConditionType) && ZoneId is { } zoneId
            && !await db.Zones.AnyAsync(z => z.Id == zoneId && z.CameraId == CameraId, ct))
            conditionError = "That zone isn't on the chosen camera.";
        if (conditionError is not null)
        {
            ErrorMessage = conditionError;
            await LoadPickersAsync(ct);
            return Page();
        }

        AlertRule rule;
        if (Id is { } id)
        {
            var existing = await db.AlertRules.Include(r => r.Deliveries).FirstOrDefaultAsync(r => r.Id == id, ct);
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

        rule.Name = Name;
        rule.IsEnabled = IsEnabled;
        rule.ConditionType = ConditionType;
        rule.CameraId = ConditionType == AlertConditionType.CameraNotReporting || IsDetection(ConditionType) ? CameraId : null;
        rule.NodeId = ConditionType is AlertConditionType.NodeOffline or AlertConditionType.NodeStorageLow
            or AlertConditionType.NodeFailoverActivated ? NodeId : null;
        rule.ThresholdPercent = ConditionType == AlertConditionType.NodeStorageLow ? ThresholdPercent : null;
        rule.CooldownMinutes = CooldownMinutes;
        var detection = IsDetection(ConditionType);
        rule.CategoryFilter = detection ? NullIfBlank(CategoryFilter) : null;
        rule.LabelFilter = detection ? NullIfBlank(LabelFilter)?.ToLowerInvariant() : null;
        rule.ColorFilter = detection ? ObjectColors.Find(ColorFilter)?.Name : null;
        rule.ZoneId = detection ? ZoneId : null;
        rule.SpeedThresholdKph = ConditionType == AlertConditionType.SpeedOver
            ? Math.Round(SpeedUnits.ToKph(SpeedThreshold, await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct)), 2)
            : null;
        var dayFlags = ScheduleDays.Distinct().Aggregate(DayOfWeekFlags.None, (acc, d) => acc | DayFlag(d));
        rule.ScheduleDays = detection && dayFlags is not (DayOfWeekFlags.None or DayOfWeekFlags.All) ? dayFlags : null;
        rule.ScheduleStart = detection && ScheduleStart != ScheduleEnd ? ScheduleStart : null;
        rule.ScheduleEnd = detection && ScheduleStart != ScheduleEnd ? ScheduleEnd : null;
        rule.LastModifiedAt = DateTime.UtcNow;
        rule.LastModifiedBy = User.Identity?.Name;

        ReconcileDeliveries(rule);

        await db.SaveChangesAsync(ct);
        await auditService.LogAsync(Id is null ? "AlertRule.Create" : "AlertRule.Update",
            User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(), rule.Name, ct);
        detectionAlerts.InvalidateRules();

        return RedirectToPage("Index");
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

    private static DayOfWeekFlags DayFlag(DayOfWeek d) => (DayOfWeekFlags)(1 << (int)d);

    private async Task LoadPickersAsync(CancellationToken ct)
    {
        Cameras = (await cameraService.ListAsync(ct)).OrderBy(c => c.Name).ToList();
        Nodes = (await nodeService.ListAsync(ct)).OrderBy(n => n.Name).ToList();
        Categories = await timelineService.GetDetectedObjectCategoriesAsync(ct);
        Labels = await timelineService.GetDetectedObjectLabelsAsync(ct);
        Zones = (await db.Zones.AsNoTracking().Where(z => z.Kind != ZoneKind.Privacy)
                .OrderBy(z => z.Name).Select(z => new { z.Id, z.CameraId, z.Name }).ToListAsync(ct))
            .Select(z => (z.Id, z.CameraId, z.Name)).ToList();
        SpeedUnit = SpeedUnits.Normalize(await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct));
    }
}
