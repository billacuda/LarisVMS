using System.Globalization;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Turns hourly event counts into an activity heatmap (see ActivityHeatmapDto). Pure, so the
/// time-zone folding, views and filtering are unit-testable without a database.</summary>
public static class ActivityHeatmapBuilder
{
    public const string MotionGroup = "Motion";
    public const string TagsGroup = "Custom tags";
    /// <summary>Sent when every event type is unticked — an explicit "nothing", not "the default".</summary>
    public const string NoneSelected = "__none__";

    public static readonly IReadOnlyList<string> Ranges = ["24h", "7d", "30d", "90d", "1y"];

    public static string NormalizeRange(string? range) => range is not null && Ranges.Contains(range) ? range : "7d";

    /// <summary>"By day" only makes sense from 30 days up; anything else is the typical-week pattern.</summary>
    public static bool IsCalendar(string range, string? view) => view == "calendar" && range is "30d" or "90d" or "1y";

    public static TimeSpan RangeSpan(string range) => range switch
    {
        "24h" => TimeSpan.FromHours(24),
        "30d" => TimeSpan.FromDays(30),
        "90d" => TimeSpan.FromDays(90),
        "1y" => TimeSpan.FromDays(365),
        _ => TimeSpan.FromDays(7),
    };

    /// <summary>Null selection means the default: every event type except plain motion, which would
    /// otherwise drown out the objects — unless motion is all there is.</summary>
    public static List<string> DefaultSelection(IReadOnlyList<ActivityGroupDto> groups)
    {
        var objects = groups.Where(g => g.Key != MotionGroup).Select(g => g.Key).ToList();
        return objects.Count > 0 ? objects : groups.Select(g => g.Key).ToList();
    }

    /// <summary>The selection as it applies to these groups. Keys this camera doesn't have are dropped
    /// — the choice carries over between cameras — and when nothing of it is left, the default is used
    /// rather than showing a blank chart. An explicit "nothing ticked" stays empty.</summary>
    internal static HashSet<string> ResolveSelection(IReadOnlyList<string>? selection, IReadOnlyList<ActivityGroupDto> groups)
    {
        if (selection is { Count: > 0 } && selection.Contains(NoneSelected)) return [NoneSelected];
        var known = groups.Select(g => g.Key).Concat(groups.SelectMany(g => g.Labels.Select(l => l.Key)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applicable = (selection ?? []).Where(known.Contains).ToList();
        return (applicable.Count > 0 ? applicable : DefaultSelection(groups)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static ActivityHeatmapDto Build(Guid cameraId, string cameraName, TimeZoneInfo zone, string range,
        DateTime nowUtc, IReadOnlyList<ActivityHourCount> counts, IReadOnlyList<string>? selection,
        DayOfWeek firstDayOfWeek = DayOfWeek.Sunday, string? view = null)
    {
        range = NormalizeRange(range);
        var groups = counts
            .GroupBy(c => c.Group)
            .Select(g => new ActivityGroupDto(g.Key, g.Key, Emoji(g.Key), g.Sum(c => c.Count),
                g.Where(c => c.Label is not null)
                    .GroupBy(c => c.Label!, StringComparer.OrdinalIgnoreCase)
                    .Select(l => new ActivityLabelDto($"{g.Key}:{l.Key}", l.Key, l.Sum(c => c.Count)))
                    .OrderByDescending(l => l.Count).ThenBy(l => l.Name).ToList()))
            .OrderBy(g => GroupOrder(g.Key)).ThenByDescending(g => g.Count).ThenBy(g => g.Name)
            .ToList();

        var selected = ResolveSelection(selection, groups);
        bool Included(ActivityHourCount c) =>
            selected.Contains(c.Group) || (c.Label is not null && selected.Contains($"{c.Group}:{c.Label}"));
        var included = counts.Where(Included).ToList();

        if (range == "24h") return BuildDay(cameraId, cameraName, zone, nowUtc, included, groups, selected);

        var fromUtc = ActivityRollupService.FloorHour(nowUtc - RangeSpan(range));
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone));
        var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        var dayTotals = new Dictionary<DateOnly, int>();
        foreach (var c in included)
        {
            var date = DateOnly.FromDateTime(Local(c.HourUtc, zone));
            dayTotals[date] = dayTotals.GetValueOrDefault(date) + c.Count;
        }
        string? busiestText = null;
        if (dayTotals.Count > 0)
        {
            var b = dayTotals.OrderByDescending(d => d.Value).ThenBy(d => d.Key).First();
            if (b.Value > 0) busiestText = $"Busiest day: {b.Key.ToString("ddd, MMM d", CultureInfo.InvariantCulture)} ({b.Value:N0} events).";
        }

        return IsCalendar(range, view)
            ? BuildCalendar(cameraId, cameraName, zone, range, included, groups, selected, firstDate, lastDate, busiestText)
            : BuildWeek(cameraId, cameraName, zone, range, included, groups, selected, firstDayOfWeek, firstDate, lastDate, busiestText);
    }

    private static DateTime Local(DateTime hourUtc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hourUtc, DateTimeKind.Utc), zone);

    private static ActivityHeatmapDto BuildWeek(Guid cameraId, string cameraName, TimeZoneInfo zone, string range,
        List<ActivityHourCount> counts, List<ActivityGroupDto> groups, HashSet<string> selected, DayOfWeek firstDay,
        DateOnly firstDate, DateOnly lastDate, string? busiestDay)
    {
        var days = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)firstDay + i) % 7)).ToList();
        var cells = days.Select(_ => new int[24]).ToArray();
        foreach (var c in counts)
        {
            var local = Local(c.HourUtc, zone);
            cells[days.IndexOf(local.DayOfWeek)][local.Hour] += c.Count;
        }

        // How many Mondays, Tuesdays, … the range covers — the page divides by these for averages.
        var occurrences = new int[7];
        for (var d = firstDate; d <= lastDate; d = d.AddDays(1)) occurrences[days.IndexOf(d.DayOfWeek)]++;

        var dayNames = CultureInfo.InvariantCulture.DateTimeFormat.DayNames;
        var max = cells.Max(r => r.Max());
        string? peak = null;
        if (max > 0)
        {
            var (row, col) = Enumerable.Range(0, 7).SelectMany(r => Enumerable.Range(0, 24).Select(h => (r, h)))
                .First(p => cells[p.r][p.h] == max);
            var busiestHour = Enumerable.Range(0, 24).OrderByDescending(h => cells.Sum(r => r[h])).First();
            peak = $"Busiest: {dayNames[(int)days[row]]}s {HourRange(col)} ({max:N0} events). " +
                   $"Busiest time of day overall: {HourRange(busiestHour)}.";
        }

        return new ActivityHeatmapDto(cameraId, cameraName, ZoneName(zone), range, "week",
            days.Select(d => dayNames[(int)d][..3]).ToList(),
            Enumerable.Range(0, 24).Select(HourLabel).ToList(),
            cells.Select(r => r.ToList()).ToList(), max, cells.Sum(r => r.Sum()),
            groups, selected.ToList(), peak, busiestDay, WeekdayOccurrences: occurrences.ToList());
    }

    /// <summary>"By day": one column per local date in the range, hours top to bottom.</summary>
    private static ActivityHeatmapDto BuildCalendar(Guid cameraId, string cameraName, TimeZoneInfo zone, string range,
        List<ActivityHourCount> counts, List<ActivityGroupDto> groups, HashSet<string> selected,
        DateOnly firstDate, DateOnly lastDate, string? busiestDay)
    {
        var dates = new List<DateOnly>();
        for (var d = firstDate; d <= lastDate; d = d.AddDays(1)) dates.Add(d);
        var index = dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);

        var cells = Enumerable.Range(0, 24).Select(_ => new int[dates.Count]).ToArray();
        var totals = new int[dates.Count];
        foreach (var c in counts)
        {
            var local = Local(c.HourUtc, zone);
            if (!index.TryGetValue(DateOnly.FromDateTime(local), out var col)) continue;
            cells[local.Hour][col] += c.Count;
            totals[col] += c.Count;
        }

        var max = cells.Length == 0 || dates.Count == 0 ? 0 : cells.Max(r => r.Max());
        string? peak = null;
        if (max > 0)
        {
            var (hour, col) = Enumerable.Range(0, 24).SelectMany(h => Enumerable.Range(0, dates.Count).Select(c => (h, c)))
                .First(p => cells[p.h][p.c] == max);
            peak = $"Busiest hour: {dates[col].ToString("ddd, MMM d", CultureInfo.InvariantCulture)}, {HourRange(hour)} ({max:N0} events).";
        }

        return new ActivityHeatmapDto(cameraId, cameraName, ZoneName(zone), range, "calendar",
            Enumerable.Range(0, 24).Select(HourLabel).ToList(),
            dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList(),
            cells.Select(r => r.ToList()).ToList(), max, totals.Sum(),
            groups, selected.ToList(), peak, busiestDay, DayTotals: totals.ToList());
    }

    /// <summary>Last 24 hours: one row per selected event type, one column per hour ending with the
    /// current one.</summary>
    private static ActivityHeatmapDto BuildDay(Guid cameraId, string cameraName, TimeZoneInfo zone, DateTime nowUtc,
        List<ActivityHourCount> counts, List<ActivityGroupDto> groups, HashSet<string> selected)
    {
        var endHourUtc = ActivityRollupService.FloorHour(nowUtc);
        var startHourUtc = endHourUtc.AddHours(-23);
        var rowGroups = groups.Where(g => counts.Any(c => c.Group == g.Key)).Select(g => g.Key).ToList();
        var cells = rowGroups.Select(_ => new int[24]).ToArray();
        foreach (var c in counts)
        {
            var col = (int)(DateTime.SpecifyKind(c.HourUtc, DateTimeKind.Utc) - startHourUtc).TotalHours;
            if (col is < 0 or > 23) continue;
            cells[rowGroups.IndexOf(c.Group)][col] += c.Count;
        }

        var columns = Enumerable.Range(0, 24)
            .Select(i => HourLabel(TimeZoneInfo.ConvertTimeFromUtc(startHourUtc.AddHours(i), zone).Hour))
            .ToList();
        var max = cells.Length == 0 ? 0 : cells.Max(r => r.Max());
        string? peak = null;
        if (max > 0)
        {
            var totals = Enumerable.Range(0, 24).Select(h => cells.Sum(r => r[h])).ToList();
            var busiest = totals.IndexOf(totals.Max());
            var hour = TimeZoneInfo.ConvertTimeFromUtc(startHourUtc.AddHours(busiest), zone).Hour;
            peak = $"Busiest hour: {HourRange(hour)} ({totals[busiest]:N0} events).";
        }

        return new ActivityHeatmapDto(cameraId, cameraName, ZoneName(zone), "24h", "day",
            rowGroups, columns, cells.Select(r => r.ToList()).ToList(), max, cells.Sum(r => r.Sum()),
            groups, selected.ToList(), peak);
    }

    private static int GroupOrder(string group) => group switch
    {
        "Human" => 0,
        "Vehicle" => 1,
        "Animal" => 2,
        "Object" => 3,
        TagsGroup => 8,
        MotionGroup => 9,
        _ => 4,
    };

    private static string? Emoji(string group) => group switch
    {
        MotionGroup => EventColors.MotionEmoji,
        TagsGroup => "🏷️",
        _ => CocoCategoryMap.Emoji(group),
    };

    internal static string HourLabel(int hour) => hour switch
    {
        0 => "12a",
        12 => "12p",
        < 12 => $"{hour}a",
        _ => $"{hour - 12}p",
    };

    internal static string HourRange(int hour) => $"{Clock(hour)}–{Clock((hour + 1) % 24)}";

    internal static string Clock(int hour) => hour switch
    {
        0 => "12 AM",
        12 => "12 PM",
        < 12 => $"{hour} AM",
        _ => $"{hour - 12} PM",
    };

    internal static string ZoneName(TimeZoneInfo zone) => zone.IsDaylightSavingTime(DateTime.UtcNow) ? zone.DaylightName : zone.StandardName;
}
