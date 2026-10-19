using System.Text.Json;
using LarisVMS.Core;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <summary>A detection alert rule as DetectionAlertService evaluates it: the AlertRule's filter
/// columns plus its zone polygon, already parsed. ZonePolygon is null when the rule has no zone
/// filter; HasZoneFilter with an empty polygon (zone deleted or unreadable) never matches.</summary>
public sealed record DetectionRule(
    Guid Id, string Name, AlertConditionType Condition, Guid? CameraId,
    string? Category, string? Label, string? Color,
    bool HasZoneFilter, IReadOnlyList<(double X, double Y)>? ZonePolygon,
    double? SpeedThresholdKph,
    DayOfWeekFlags? ScheduleDays, TimeOnly? ScheduleStart, TimeOnly? ScheduleEnd,
    // A node-scoped rule (NodeId set, no CameraId): the cameras on that node when rules were loaded.
    IReadOnlySet<Guid>? ScopeCameraIds = null,
    // The zone the schedule is read in; null = the web server's local time.
    TimeZoneInfo? TimeZone = null,
    // MotionDetected only: the server motion zone the motion has to come from (null = anywhere), and
    // its name for the message.
    Guid? MotionZoneId = null, string? MotionZoneName = null);

/// <summary>Pure matching for detection and motion alerts (ObjectDetected / SpeedOver /
/// MotionDetected) — which events and which objects in them a rule's filters accept. No database,
/// no clock of its own.</summary>
public static class DetectionAlertMatcher
{
    /// <summary>The rule's schedule at <paramref name="nowUtc"/>, read in the rule's own time zone.</summary>
    public static bool InSchedule(DetectionRule rule, DateTime nowUtc)
        => InSchedule(rule.ScheduleDays, rule.ScheduleStart, rule.ScheduleEnd, LocalTimeFor(rule.TimeZone, nowUtc));

    /// <summary>Whether <paramref name="localTime"/> is inside the schedule. No days / no times =
    /// always. A window whose end is before its start crosses midnight and belongs to the day it
    /// starts on, the same as ScheduleWindow.</summary>
    public static bool InSchedule(DayOfWeekFlags? days, TimeOnly? start, TimeOnly? end, DateTime localTime)
    {
        var anyDay = days is null or DayOfWeekFlags.None;
        bool DayOn(DayOfWeek d) => anyDay || days!.Value.HasFlag(Flag(d));

        var time = TimeOnly.FromDateTime(localTime);
        if (start is not { } s || end is not { } e || s == e) return DayOn(localTime.DayOfWeek);

        if (s < e) return time >= s && time < e && DayOn(localTime.DayOfWeek);
        // Crosses midnight: the evening part belongs to today, the early-morning part to yesterday.
        return (time >= s && DayOn(localTime.DayOfWeek))
            || (time < e && DayOn(localTime.AddDays(-1).DayOfWeek));
    }

    public static DateTime LocalTimeFor(TimeZoneInfo? zone, DateTime nowUtc)
        => zone is null ? nowUtc.ToLocalTime() : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);

    /// <summary>Resolves a stored time zone id (IANA or Windows). Null or unknown = server local.</summary>
    public static TimeZoneInfo? ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    /// <summary>Span-level filters: camera and category.</summary>
    public static bool MatchesEvent(DetectionRule rule, DetectionEvent e) =>
        InScope(rule, e.CameraId)
        && (string.IsNullOrWhiteSpace(rule.Category) || string.Equals(rule.Category, e.Category, StringComparison.OrdinalIgnoreCase));

    /// <summary>A MotionDetected rule's filters: camera scope, and the zone when it names one.</summary>
    public static bool MatchesMotion(DetectionRule rule, MotionEvent m) =>
        rule.Condition == AlertConditionType.MotionDetected
        && InScope(rule, m.CameraId)
        && (rule.MotionZoneId is not { } zoneId || m.ZoneId == zoneId);

    private static bool InScope(DetectionRule rule, Guid cameraId) =>
        rule.CameraId is { } ruleCamera ? ruleCamera == cameraId : rule.ScopeCameraIds is null || rule.ScopeCameraIds.Contains(cameraId);

    /// <summary>The event's objects that pass the rule's label, color and zone filters. An event
    /// with no per-object records (an older Vision build) stands in as one object made from the
    /// span's own label and best box — it has no colors, so a color filter never matches it.</summary>
    public static List<DetectionEventObject> MatchingObjects(DetectionRule rule, DetectionEvent e)
    {
        var objects = e.Objects.Count > 0
            ? e.Objects
            : [new DetectionEventObject(0, e.Label, e.BoxX, e.BoxY, e.BoxW, e.BoxH, null, null, null, null, null)];
        return objects.Where(o => ObjectPasses(rule, e, o)).ToList();
    }

    public static bool ObjectPasses(DetectionRule rule, DetectionEvent e, DetectionEventObject o)
    {
        if (!string.IsNullOrWhiteSpace(rule.Label)
            && !string.Equals(rule.Label, string.IsNullOrEmpty(o.Label) ? e.Label : o.Label, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(rule.Color)
            && !string.Equals(rule.Color, o.PrimaryColor, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rule.Color, o.UpperColor, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rule.Color, o.LowerColor, StringComparison.OrdinalIgnoreCase))
            return false;

        if (rule.HasZoneFilter)
        {
            if (rule.ZonePolygon is not { Count: >= 3 } polygon) return false;
            if (o is not { X: { } x, Y: { } y, W: { } w, H: { } h }) return false;
            // Bottom centre: where the object stands, so a person's head poking over a zone edge
            // doesn't count as being in it.
            if (!PointInPolygon(x + w / 2, y + h, polygon)) return false;
        }
        return true;
    }

    /// <summary>"Vehicle — car (red)", "Human (blue shirt, black pants)", "Animal — dog".</summary>
    public static string Describe(string category, string label, DetectionEventObject? o)
    {
        var text = CocoCategoryMap.DisplayLabel(category, string.IsNullOrEmpty(o?.Label) ? label : o.Label);
        if (o is null) return text;
        if (o.UpperColor is not null || o.LowerColor is not null)
        {
            var parts = new List<string>();
            if (o.UpperColor is not null) parts.Add($"{o.UpperColor} shirt");
            if (o.LowerColor is not null) parts.Add($"{o.LowerColor} pants");
            return $"{text} ({string.Join(", ", parts)})";
        }
        return o.PrimaryColor is not null ? $"{text} ({o.PrimaryColor})" : text;
    }

    /// <summary>Parses a zone's <c>{"points":[[x,y],...]}</c>; empty when unreadable.</summary>
    public static IReadOnlyList<(double X, double Y)> ParsePolygon(string? polygonJson)
    {
        if (string.IsNullOrWhiteSpace(polygonJson)) return [];
        try
        {
            using var doc = JsonDocument.Parse(polygonJson);
            if (!doc.RootElement.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array) return [];
            var result = new List<(double, double)>();
            foreach (var p in points.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2
                    && p[0].ValueKind == JsonValueKind.Number && p[1].ValueKind == JsonValueKind.Number)
                    result.Add((p[0].GetDouble(), p[1].GetDouble()));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // Even-odd ray casting, the same test ZoneRasterizer uses for motion zones.
    private static bool PointInPolygon(double px, double py, IReadOnlyList<(double X, double Y)> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    private static DayOfWeekFlags Flag(DayOfWeek d) => d switch
    {
        DayOfWeek.Sunday => DayOfWeekFlags.Sunday,
        DayOfWeek.Monday => DayOfWeekFlags.Monday,
        DayOfWeek.Tuesday => DayOfWeekFlags.Tuesday,
        DayOfWeek.Wednesday => DayOfWeekFlags.Wednesday,
        DayOfWeek.Thursday => DayOfWeekFlags.Thursday,
        DayOfWeek.Friday => DayOfWeekFlags.Friday,
        _ => DayOfWeekFlags.Saturday,
    };
}
