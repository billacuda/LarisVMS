using System.Globalization;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Turns hourly speed-trap measurements into the speed heatmap (SpeedHeatmapDto). The
/// speed counterpart of ActivityHeatmapBuilder — same ranges, views, local-time folding and hour
/// labels — but every cell accumulates a count, a sum, a maximum and an over-the-limit count, so an
/// average is the true average of the measurements, never an average of hourly averages. Pure, so it's
/// unit-testable without a database.</summary>
public static class SpeedHeatmapBuilder
{
    public static readonly IReadOnlyList<string> Metrics = ["avg", "max", "over", "count"];

    public static string NormalizeMetric(string? metric) => metric is not null && Metrics.Contains(metric) ? metric : "avg";

    private sealed class Acc
    {
        public int Count, Over;
        public double Sum, Max;

        public void Add(SpeedHourStat s)
        {
            Count += s.Count;
            Sum += s.SumKph;
            Over += s.OverLimit;
            if (s.MaxKph > Max) Max = s.MaxKph;
        }
    }

    /// <param name="traps">Every trap that can be offered (id → name and limit in km/h), including ones
    /// with no measurements in range. A trap id in <paramref name="stats"/> that isn't here was deleted.</param>
    /// <param name="selection">Trap ids to include; null/empty (or none of them known) = all.</param>
    public static SpeedHeatmapDto Build(Guid cameraId, string cameraName, TimeZoneInfo zone, string range, DateTime nowUtc,
        IReadOnlyList<SpeedHourStat> stats, IReadOnlyDictionary<Guid, (string Name, double? LimitKph)> traps,
        IReadOnlyList<string>? selection, string? metric, string? unit,
        DayOfWeek firstDayOfWeek = DayOfWeek.Sunday, string? view = null)
    {
        range = ActivityHeatmapBuilder.NormalizeRange(range);
        metric = NormalizeMetric(metric);
        unit = SpeedUnits.Normalize(unit);

        string TrapName(Guid? id) => id is { } g && traps.TryGetValue(g, out var t) ? t.Name : "(deleted trap)";
        var options = traps.Keys.Select(id => (Guid?)id)
            .Concat(stats.Select(s => s.TrapId))
            .Distinct()
            .Select(id => new SpeedTrapOptionDto(id?.ToString() ?? "", TrapName(id), stats.Where(s => s.TrapId == id).Sum(s => s.Count)))
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var known = options.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = (selection ?? []).Where(known.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var included = selected.Count == 0 ? stats.ToList() : stats.Where(s => selected.Contains(s.TrapId?.ToString() ?? "")).ToList();

        // A limit line only makes sense when every trap shown has the same one.
        var shownTraps = selected.Count == 0 ? options.Select(o => o.Id) : selected;
        var limits = shownTraps.Select(id => Guid.TryParse(id, out var g) && traps.TryGetValue(g, out var t) ? t.LimitKph : null).Distinct().ToList();
        double? limit = limits.Count == 1 && limits[0] is { } l ? Math.Round(SpeedUnits.FromKph(l, unit), 1) : null;

        var total = new Acc();
        foreach (var s in included) total.Add(s);

        double? Value(Acc a) => a.Count == 0 ? null : metric switch
        {
            "max" => Math.Round(SpeedUnits.FromKph(a.Max, unit), 1),
            "over" => a.Over,
            "count" => a.Count,
            _ => Math.Round(SpeedUnits.FromKph(a.Sum / a.Count, unit), 1),
        };

        string Describe(double v, string where) => metric switch
        {
            "max" => $"Top speed: {v:0} {SpeedUnits.Label(unit)}, {where}.",
            "over" => $"Most over the limit: {where} ({v:N0}).",
            "count" => $"Busiest: {where} ({v:N0} measured).",
            _ => $"Highest average: {where} ({v:0} {SpeedUnits.Label(unit)}).",
        };

        DateTime Local(DateTime hourUtc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(hourUtc, DateTimeKind.Utc), zone);

        string mode;
        List<string> rowLabels, columnLabels;
        Acc[][] cells;
        List<double?>? dayValues = null;
        Func<int, int, string> where;

        if (range == "24h")
        {
            mode = "day";
            var endHourUtc = ActivityRollupService.FloorHour(nowUtc);
            var startHourUtc = endHourUtc.AddHours(-23);
            var rowTraps = included.Select(s => s.TrapId).Distinct().OrderBy(id => TrapName(id), StringComparer.OrdinalIgnoreCase).ToList();
            rowLabels = rowTraps.Select(TrapName).ToList();
            columnLabels = Enumerable.Range(0, 24)
                .Select(i => ActivityHeatmapBuilder.HourLabel(TimeZoneInfo.ConvertTimeFromUtc(startHourUtc.AddHours(i), zone).Hour)).ToList();
            cells = NewCells(rowTraps.Count, 24);
            foreach (var s in included)
            {
                var col = (int)(DateTime.SpecifyKind(s.HourUtc, DateTimeKind.Utc) - startHourUtc).TotalHours;
                if (col is < 0 or > 23) continue;
                cells[rowTraps.IndexOf(s.TrapId)][col].Add(s);
            }
            where = (r, c) => $"{rowLabels[r]}, {ActivityHeatmapBuilder.HourRange(TimeZoneInfo.ConvertTimeFromUtc(startHourUtc.AddHours(c), zone).Hour)}";
        }
        else
        {
            var fromUtc = ActivityRollupService.FloorHour(nowUtc - ActivityHeatmapBuilder.RangeSpan(range));
            var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone));
            var lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));

            if (ActivityHeatmapBuilder.IsCalendar(range, view))
            {
                mode = "calendar";
                var dates = new List<DateOnly>();
                for (var d = firstDate; d <= lastDate; d = d.AddDays(1)) dates.Add(d);
                var index = dates.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
                cells = NewCells(24, dates.Count);
                var perDay = dates.Select(_ => new Acc()).ToArray();
                foreach (var s in included)
                {
                    var local = Local(s.HourUtc);
                    if (!index.TryGetValue(DateOnly.FromDateTime(local), out var col)) continue;
                    cells[local.Hour][col].Add(s);
                    perDay[col].Add(s);
                }
                rowLabels = Enumerable.Range(0, 24).Select(ActivityHeatmapBuilder.HourLabel).ToList();
                columnLabels = dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
                dayValues = perDay.Select(Value).ToList();
                where = (r, c) => $"{dates[c].ToString("ddd, MMM d", CultureInfo.InvariantCulture)}, {ActivityHeatmapBuilder.HourRange(r)}";
            }
            else
            {
                mode = "week";
                var days = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)firstDayOfWeek + i) % 7)).ToList();
                cells = NewCells(7, 24);
                foreach (var s in included)
                {
                    var local = Local(s.HourUtc);
                    cells[days.IndexOf(local.DayOfWeek)][local.Hour].Add(s);
                }
                var dayNames = CultureInfo.InvariantCulture.DateTimeFormat.DayNames;
                rowLabels = days.Select(d => dayNames[(int)d][..3]).ToList();
                columnLabels = Enumerable.Range(0, 24).Select(ActivityHeatmapBuilder.HourLabel).ToList();
                where = (r, c) => $"{dayNames[(int)days[r]]}s {ActivityHeatmapBuilder.HourRange(c)}";
            }
        }

        var values = cells.Select(row => row.Select(Value).ToList()).ToList();
        var present = values.SelectMany(v => v).OfType<double>().ToList();
        var max = present.Count == 0 ? 0 : present.Max();
        var min = present.Count == 0 || metric is "over" or "count" ? 0 : present.Min();

        string? peak = null;
        if (present.Count > 0 && max > 0)
        {
            var (row, col) = Enumerable.Range(0, values.Count)
                .SelectMany(r => Enumerable.Range(0, values[r].Count).Select(c => (r, c)))
                .First(p => values[p.r][p.c] == max);
            peak = Describe(max, where(row, col));
        }

        return new SpeedHeatmapDto(cameraId, cameraName, ActivityHeatmapBuilder.ZoneName(zone), range, mode, metric, SpeedUnits.Label(unit),
            rowLabels, columnLabels, values, min, max, total.Count,
            total.Count == 0 ? null : Math.Round(SpeedUnits.FromKph(total.Sum / total.Count, unit), 1),
            total.Count == 0 ? null : Math.Round(SpeedUnits.FromKph(total.Max, unit), 1),
            options, selected.ToList(), peak, limit, dayValues);
    }

    private static Acc[][] NewCells(int rows, int cols) =>
        Enumerable.Range(0, rows).Select(_ => Enumerable.Range(0, cols).Select(_ => new Acc()).ToArray()).ToArray();
}
