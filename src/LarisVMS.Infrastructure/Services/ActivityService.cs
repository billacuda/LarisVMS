using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Activity heatmaps for one camera or all of them.
///
/// Counts come from two places: finished hours already rolled up into ActivityHourlyCount (built once
/// by ActivityRollupService and kept long after the footage — and MotionSpan rows — are gone), plus
/// the hours since the rollup watermark, counted live from MotionSpan. The rolled-up part only changes
/// when the watermark moves (hourly), so it's cached until then: a repeat load costs one small live
/// query for the latest hour or two.</summary>
public class ActivityService(ApplicationDbContext db, ActivityRollupService rollup, IMemoryCache cache,
    SpeedRollupService? speedRollup = null) : IActivityService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(2);

    public async Task<ActivityHeatmapDto?> GetHeatmapAsync(Guid? cameraId, IReadOnlySet<Guid>? accessible, string? range, string? view,
        IReadOnlyList<string>? selection, CancellationToken ct = default)
    {
        range = ActivityHeatmapBuilder.NormalizeRange(range);
        var now = DateTime.UtcNow;
        var from = ActivityRollupService.FloorHour(now - ActivityHeatmapBuilder.RangeSpan(range));

        string name;
        TimeZoneInfo zone;
        IReadOnlyCollection<Guid>? cameraIds;
        if (cameraId is { } id)
        {
            if (accessible is not null && !accessible.Contains(id)) return null;
            var camera = await db.Cameras.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.Name, c.TimeZoneId }).FirstOrDefaultAsync(ct);
            if (camera is null) return null;
            name = camera.Name;
            zone = ResolveZone(camera.TimeZoneId);
            cameraIds = [id];
        }
        else
        {
            // All cameras: every camera this viewer can review, in the server's time zone (the
            // cameras may each have their own).
            name = "All cameras";
            zone = TimeZoneInfo.Local;
            cameraIds = accessible;
        }

        var watermark = await rollup.GetWatermarkAsync(ct);
        var historyEnd = watermark is { } w && w > from ? (w < now ? w : now) : from;

        var counts = new List<ActivityHourCount>();
        if (historyEnd > from) counts.AddRange(await HistoryAsync(cameraIds, from, historyEnd, watermark, ct));
        var tail = await ActivityRollupService.CountSpansAsync(db, cameraIds, historyEnd, now, ct);
        counts.AddRange(tail.GroupBy(t => (t.HourUtc, t.Group, t.Label))
            .Select(g => new ActivityHourCount(g.Key.HourUtc, g.Key.Group, g.Key.Label, g.Sum(x => x.Count))));

        return ActivityHeatmapBuilder.Build(cameraId ?? Guid.Empty, name, zone, range, now, counts, selection,
            CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, view);
    }

    public async Task<SceneHeatDto?> GetSceneHeatAsync(Guid cameraId, IReadOnlySet<Guid>? accessible, string? range, string? source,
        IReadOnlyList<string>? categories, int? fromHour, int? toHour, CancellationToken ct = default)
    {
        if (accessible is not null && !accessible.Contains(cameraId)) return null;
        var camera = await db.Cameras.AsNoTracking().Where(c => c.Id == cameraId)
            .Select(c => new { c.Name, c.TimeZoneId, c.ServerMotionEnabled, c.MotionRegionMode, c.MotionGridSize }).FirstOrDefaultAsync(ct);
        if (camera is null) return null;

        range = ActivityHeatmapBuilder.NormalizeRange(range);
        var motion = string.Equals(source, "motion", StringComparison.OrdinalIgnoreCase);
        var heatSource = motion ? SceneHeatSource.Motion : SceneHeatSource.Objects;
        var now = DateTime.UtcNow;
        var from = ActivityRollupService.FloorHour(now - ActivityHeatmapBuilder.RangeSpan(range));
        var zone = ResolveZone(camera.TimeZoneId);
        var wanted = categories is { Count: > 0 } ? new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase) : null;

        var rows = await db.SceneHeatHourlies.AsNoTracking()
            .Where(s => s.CameraId == cameraId && s.Source == heatSource && s.HourUtc >= from)
            .ToListAsync(ct);
        if (!motion)
        {
            // The hours since the last rollup, straight from the objects' paths.
            var watermark = await new SceneHeatRollupService(db).GetWatermarkAsync(ct) ?? from;
            var tailFrom = watermark > from ? watermark : from;
            rows.RemoveAll(r => r.HourUtc >= tailFrom);
            rows.AddRange(await SceneHeatRollupService.AggregateAsync(db, [cameraId], tailFrom, now, ct));
        }

        var (cols, gridRows) = motion
            ? (camera.MotionGridSize, camera.MotionGridSize)
            : await SceneHeatRollupService.GridShapeAsync(db, cameraId, ct);
        var sums = new long[cols * gridRows];
        foreach (var row in rows)
        {
            if (wanted is not null && !wanted.Contains(row.Label)) continue;
            if (!InHours(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.HourUtc, DateTimeKind.Utc), zone).Hour, fromHour, toHour)) continue;
            SceneHeat.AddCellsResampled(row.Cells, row.Cols, row.Rows, sums, cols, gridRows);
        }

        var max = sums.Length == 0 ? 0 : sums.Max();
        var cells = sums.Select(c => max == 0 ? 0 : (double)c / max).ToArray();
        var motionAvailable = camera.ServerMotionEnabled && camera.MotionRegionMode == MotionRegionMode.Grid;
        return new SceneHeatDto(cameraId, camera.Name, motion ? "motion" : "objects", range, cols, gridRows, cells, max, motionAvailable);
    }

    /// <summary>Whether a local hour is in [from, to) — wrapping past midnight when from is after to
    /// (22 to 6 is the night). No bounds means every hour.</summary>
    internal static bool InHours(int hour, int? fromHour, int? toHour)
    {
        if (fromHour is not { } f || toHour is not { } t || f == t) return true;
        return f < t ? hour >= f && hour < t : hour >= f || hour < t;
    }

    public async Task<SpeedHeatmapDto?> GetSpeedHeatmapAsync(Guid? cameraId, IReadOnlySet<Guid>? accessible, string? range, string? view,
        string? metric, IReadOnlyList<string>? trapIds, CancellationToken ct = default)
    {
        range = ActivityHeatmapBuilder.NormalizeRange(range);
        var now = DateTime.UtcNow;
        var from = ActivityRollupService.FloorHour(now - ActivityHeatmapBuilder.RangeSpan(range));

        string name;
        TimeZoneInfo zone;
        IReadOnlyCollection<Guid>? cameraIds;
        if (cameraId is { } id)
        {
            if (accessible is not null && !accessible.Contains(id)) return null;
            var camera = await db.Cameras.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.Name, c.TimeZoneId }).FirstOrDefaultAsync(ct);
            if (camera is null) return null;
            name = camera.Name;
            zone = ResolveZone(camera.TimeZoneId);
            cameraIds = [id];
        }
        else
        {
            name = "All cameras";
            zone = TimeZoneInfo.Local;
            cameraIds = accessible;
        }

        var watermark = await (speedRollup ?? new SpeedRollupService(db)).GetWatermarkAsync(ct);
        var historyEnd = watermark is { } w && w > from ? (w < now ? w : now) : from;

        var stats = new List<SpeedHourStat>();
        if (historyEnd > from)
        {
            var history = db.SpeedHourlyStats.AsNoTracking().Where(s => s.HourUtc >= from && s.HourUtc < historyEnd);
            if (cameraIds is not null) history = history.Where(s => cameraIds.Contains(s.CameraId));
            var rows = await history
                .GroupBy(s => new { s.HourUtc, s.SpeedTrapId, s.Label })
                .Select(g => new { g.Key.HourUtc, g.Key.SpeedTrapId, g.Key.Label, Count = g.Sum(s => s.Count), Sum = g.Sum(s => s.SumKph), Max = g.Max(s => s.MaxKph), Over = g.Sum(s => s.OverLimitCount) })
                .ToListAsync(ct);
            stats.AddRange(rows.Select(r => new SpeedHourStat(DateTime.SpecifyKind(r.HourUtc, DateTimeKind.Utc), r.SpeedTrapId, r.Label, r.Count, r.Sum, r.Max, r.Over)));
        }
        var tail = await SpeedRollupService.AggregateAsync(db, cameraIds, historyEnd, now, ct);
        stats.AddRange(tail.GroupBy(t => (t.HourUtc, t.SpeedTrapId, t.Label)).Select(g => new SpeedHourStat(
            g.Key.HourUtc, g.Key.SpeedTrapId, g.Key.Label, g.Sum(x => x.Count), g.Sum(x => x.SumKph), g.Max(x => x.MaxKph), g.Sum(x => x.OverLimitCount))));

        // The traps on offer: this camera's, or every accessible camera's (named "Camera · Trap").
        var trapQuery = db.SpeedTraps.AsNoTracking();
        if (cameraIds is not null) trapQuery = trapQuery.Where(t => cameraIds.Contains(t.CameraId));
        var trapRows = await trapQuery.Select(t => new { t.Id, t.Name, t.SpeedLimitKph, CameraName = t.Camera.Name }).ToListAsync(ct);
        var traps = trapRows.ToDictionary(t => t.Id,
            t => (cameraId is null ? $"{t.CameraName} · {t.Name}" : t.Name, t.SpeedLimitKph));

        var unit = await db.Settings.AsNoTracking().Where(s => s.Key == LarisVMS.Core.SpeedUnits.SettingKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return SpeedHeatmapBuilder.Build(cameraId ?? Guid.Empty, name, zone, range, now, stats, traps, trapIds, metric, unit,
            CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek, view);
    }

    /// <summary>Rolled-up hours in [fromUtc, toUtc), summed across the cameras in SQL — the
    /// all-cameras heatmap returns hours × types rows no matter how many cameras there are. Cached
    /// until the watermark moves.</summary>
    private async Task<List<ActivityHourCount>> HistoryAsync(IReadOnlyCollection<Guid>? cameraIds, DateTime fromUtc, DateTime toUtc,
        DateTime? watermark, CancellationToken ct)
    {
        var scope = cameraIds is null ? "all" : string.Join(',', cameraIds.OrderBy(g => g));
        var key = $"activity-history:{scope.GetHashCode()}:{scope.Length}:{fromUtc:o}:{toUtc:o}:{watermark:o}";
        if (cache.TryGetValue(key, out List<ActivityHourCount>? hit) && hit is not null) return hit;

        var query = db.ActivityHourlyCounts.AsNoTracking().Where(a => a.HourUtc >= fromUtc && a.HourUtc < toUtc);
        if (cameraIds is not null) query = query.Where(a => cameraIds.Contains(a.CameraId));
        var rows = await query
            .GroupBy(a => new { a.HourUtc, a.Group, a.Label })
            .Select(g => new { g.Key.HourUtc, g.Key.Group, g.Key.Label, Count = g.Sum(a => a.Count) })
            .ToListAsync(ct);

        var result = rows.Select(r => new ActivityHourCount(DateTime.SpecifyKind(r.HourUtc, DateTimeKind.Utc), r.Group, r.Label, r.Count)).ToList();
        cache.Set(key, result, CacheTtl);
        return result;
    }

    /// <summary>The camera's own classes, folded into the same groups AI detection uses.</summary>
    internal static string GroupOf(DetectionKind kind) => kind switch
    {
        DetectionKind.Human or DetectionKind.Face => "Human",
        DetectionKind.Vehicle => "Vehicle",
        DetectionKind.Animal => "Animal",
        _ => "Object",
    };

    /// <summary>The camera's configured zone when it has one, otherwise the server's — the same zone
    /// every other server-rendered time on the site uses.</summary>
    internal static TimeZoneInfo ResolveZone(string? timeZoneId) =>
        !string.IsNullOrWhiteSpace(timeZoneId) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone) ? zone : TimeZoneInfo.Local;
}
