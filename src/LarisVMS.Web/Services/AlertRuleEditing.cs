using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>What either rule editor submits — the admin's (Admin/Alerts/Edit) and a user's own
/// (My notifications). SpeedThreshold is in the display unit; ScheduleDays empty = every day.</summary>
public sealed record AlertRuleInput(
    string Name, bool IsEnabled, AlertConditionType ConditionType, Guid? CameraId, Guid? NodeId,
    int ThresholdPercent, int CooldownMinutes,
    string? CategoryFilter, string? LabelFilter, string? ColorFilter, Guid? ZoneId, double SpeedThreshold,
    IReadOnlyCollection<DayOfWeek> ScheduleDays, TimeOnly? ScheduleStart, TimeOnly? ScheduleEnd, string? TimeZoneId);

/// <summary>Validation and field mapping shared by both rule editors, so a personal notification
/// and an admin's alert rule follow exactly the same rules.</summary>
public static class AlertRuleEditing
{
    public static bool IsDetection(AlertConditionType t) => t is AlertConditionType.ObjectDetected or AlertConditionType.SpeedOver;

    /// <summary>Evaluated as events arrive (detections and motion): these take a zone and a schedule.</summary>
    public static bool IsEventDriven(AlertConditionType t) => IsDetection(t) || t == AlertConditionType.MotionDetected;

    public static bool IsEvidenceLock(AlertConditionType t) => t is AlertConditionType.EvidenceLockExpiringSoon or AlertConditionType.EvidenceLockRemoved;

    public static bool IsNodeCondition(AlertConditionType t)
        => t is AlertConditionType.NodeOffline or AlertConditionType.NodeStorageLow or AlertConditionType.NodeFailoverActivated;

    /// <summary>Types whose scope is "a camera, a node's cameras, or every camera".</summary>
    public static bool HasCameraScope(AlertConditionType t) => IsEventDriven(t) || IsEvidenceLock(t);

    /// <summary>Null when the input is valid, otherwise a message for the form.</summary>
    public static async Task<string?> ValidateAsync(ApplicationDbContext db, AlertRuleInput i, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(i.Name)) return "Enter a name.";
        if (i.CooldownMinutes < 1) return "Cooldown must be at least 1 minute.";

        var error = i.ConditionType switch
        {
            AlertConditionType.CameraNotReporting when i.CameraId is null => "Choose a camera.",
            _ when IsNodeCondition(i.ConditionType) && i.NodeId is null => "Choose a node.",
            AlertConditionType.NodeStorageLow when i.ThresholdPercent is < 1 or > 99 => "Threshold must be between 1 and 99.",
            AlertConditionType.SpeedOver when i.SpeedThreshold is <= 0 or > 400 => "Enter a speed above 0.",
            _ when IsEventDriven(i.ConditionType) && i.ZoneId is not null && i.CameraId is null => "A zone filter needs a camera.",
            _ when IsEventDriven(i.ConditionType) && (i.ScheduleStart is null) != (i.ScheduleEnd is null) => "Enter both a start and an end time, or neither.",
            _ when IsDetection(i.ConditionType) && !string.IsNullOrWhiteSpace(i.ColorFilter) && !ObjectColors.IsKnown(i.ColorFilter) => "Choose a color from the list.",
            _ => null,
        };
        if (error is null && IsEventDriven(i.ConditionType) && i.ZoneId is { } zoneId
            && !await db.Zones.AnyAsync(z => z.Id == zoneId && z.CameraId == i.CameraId, ct))
            error = "That zone isn't on the chosen camera.";
        // Motion carries the zone that saw it, and only server motion zones see motion.
        if (error is null && i.ConditionType == AlertConditionType.MotionDetected && i.ZoneId is { } motionZoneId
            && !await db.Zones.AnyAsync(z => z.Id == motionZoneId && z.Kind == ZoneKind.ServerMotion, ct))
            error = "Choose a server motion zone.";
        return error;
    }

    public static async Task ApplyAsync(AlertRule rule, AlertRuleInput i, ISettingsResolver settings, string? modifiedBy, CancellationToken ct)
    {
        var detection = IsDetection(i.ConditionType);
        var eventDriven = IsEventDriven(i.ConditionType);
        var cameraScoped = HasCameraScope(i.ConditionType);

        rule.Name = i.Name.Trim();
        rule.IsEnabled = i.IsEnabled;
        rule.ConditionType = i.ConditionType;
        rule.CameraId = i.ConditionType == AlertConditionType.CameraNotReporting || cameraScoped ? i.CameraId : null;
        // Node conditions need a node; camera-scoped types may limit to a node when no camera is chosen.
        rule.NodeId = IsNodeCondition(i.ConditionType) || (cameraScoped && i.CameraId is null) ? i.NodeId : null;
        rule.ThresholdPercent = i.ConditionType == AlertConditionType.NodeStorageLow ? i.ThresholdPercent : null;
        rule.CooldownMinutes = i.CooldownMinutes;
        rule.CategoryFilter = detection ? NullIfBlank(i.CategoryFilter) : null;
        rule.LabelFilter = detection ? NullIfBlank(i.LabelFilter)?.ToLowerInvariant() : null;
        rule.ColorFilter = detection ? ObjectColors.Find(i.ColorFilter)?.Name : null;
        rule.ZoneId = eventDriven && i.CameraId is not null ? i.ZoneId : null;
        rule.SpeedThresholdKph = i.ConditionType == AlertConditionType.SpeedOver
            ? Math.Round(SpeedUnits.ToKph(i.SpeedThreshold, await settings.GetAsync(SpeedUnits.SettingKey, SpeedUnits.Default, ct: ct)), 2)
            : null;
        var dayFlags = i.ScheduleDays.Distinct().Aggregate(DayOfWeekFlags.None, (acc, d) => acc | DayFlag(d));
        rule.ScheduleDays = eventDriven && dayFlags is not (DayOfWeekFlags.None or DayOfWeekFlags.All) ? dayFlags : null;
        rule.ScheduleStart = eventDriven && i.ScheduleStart != i.ScheduleEnd ? i.ScheduleStart : null;
        rule.ScheduleEnd = eventDriven && i.ScheduleStart != i.ScheduleEnd ? i.ScheduleEnd : null;
        // Only a zone the server knows is stored; anything else falls back to the server's own time.
        rule.TimeZoneId = eventDriven && DetectionAlertMatcher.ResolveTimeZone(i.TimeZoneId) is not null ? i.TimeZoneId : null;
        rule.LastModifiedAt = DateTime.UtcNow;
        rule.LastModifiedBy = modifiedBy;
    }

    public static List<DayOfWeek> DaysOf(DayOfWeekFlags? days)
        => days is { } d and not DayOfWeekFlags.None ? Enum.GetValues<DayOfWeek>().Where(x => d.HasFlag(DayFlag(x))).ToList() : [];

    /// <summary>A one-line summary for lists: "Animal · Back yard · 22:00–06:00 · Mon, Tue".</summary>
    public static string Describe(AlertRule r, IReadOnlyDictionary<Guid, string> cameraNames, IReadOnlyDictionary<Guid, string> nodeNames)
    {
        var parts = new List<string> { NotificationSettings.LabelOf(r.ConditionType) };
        if (!string.IsNullOrEmpty(r.LabelFilter)) parts.Add(r.LabelFilter);
        else if (!string.IsNullOrEmpty(r.CategoryFilter)) parts.Add(r.CategoryFilter);
        if (!string.IsNullOrEmpty(r.ColorFilter)) parts.Add(ObjectColors.Display(r.ColorFilter));
        if (r.CameraId is { } c) parts.Add(cameraNames.GetValueOrDefault(c, "(deleted camera)"));
        else if (r.NodeId is { } n) parts.Add((IsNodeCondition(r.ConditionType) ? "" : "cameras on ") + nodeNames.GetValueOrDefault(n, "(deleted node)"));
        else if (HasCameraScope(r.ConditionType)) parts.Add("all cameras");
        if (r.ScheduleStart is { } s && r.ScheduleEnd is { } e) parts.Add($"{s:HH\\:mm}–{e:HH\\:mm}");
        if (r.ScheduleDays is { } days and not DayOfWeekFlags.None)
            parts.Add(string.Join(", ", DaysOf(days).Select(d => d.ToString()[..3])));
        return string.Join(" · ", parts);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DayOfWeekFlags DayFlag(DayOfWeek d) => (DayOfWeekFlags)(1 << (int)d);
}
