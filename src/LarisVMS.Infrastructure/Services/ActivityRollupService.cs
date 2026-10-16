using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Builds the activity heatmaps' long-term history (ActivityHourlyCount) from MotionSpan rows,
/// one finished hour at a time.
///
/// A watermark (setting Activity.RolledThroughUtc) records the first hour not yet rolled up. Each run
/// rolls every finished hour from 48 hours before the watermark up to the current hour — the overlap
/// catches spans a node reported late (an outage, offline checkpointing). The very first run starts
/// from the oldest span still in the database, which backfills whatever history survives.
///
/// Re-rolling never lowers a count: MotionSpan rows are only ever deleted by retention, never edited
/// back, so an hour whose spans have since been deleted keeps what it had. The one exception is the
/// "AI detection wins over the camera's own classes" rule, applied per camera per hour: once an hour
/// has AI rows, that camera's own classifications for the hour are removed so nothing counts twice.
///
/// Runs hourly (ActivityRollupBackgroundService) and at the start of every MotionSpan retention sweep,
/// so no span is deleted before it's been counted.</summary>
public class ActivityRollupService(ApplicationDbContext db)
{
    public const string WatermarkKey = "Activity.RolledThroughUtc";
    public const string HistoryDaysKey = "Activity.HistoryDays";
    public const int DefaultHistoryDays = 400;
    internal static readonly TimeSpan ReRollWindow = TimeSpan.FromHours(48);
    private static readonly TimeSpan Chunk = TimeSpan.FromDays(7);

    public static DateTime FloorHour(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    /// <summary>The first hour not yet rolled up, or null before the first run.</summary>
    public async Task<DateTime?> GetWatermarkAsync(CancellationToken ct = default)
    {
        var raw = await db.Settings.AsNoTracking().Where(s => s.Key == WatermarkKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var w)
            ? DateTime.SpecifyKind(w, DateTimeKind.Utc) : null;
    }

    public async Task RollupAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var through = FloorHour(nowUtc); // hours before this are finished
        var watermark = await GetWatermarkAsync(ct);
        DateTime from;
        if (watermark is { } w) from = w - ReRollWindow;
        else
        {
            var oldest = await db.MotionSpans.AsNoTracking().OrderBy(m => m.StartUtc).Select(m => (DateTime?)m.StartUtc).FirstOrDefaultAsync(ct);
            from = oldest is { } o ? FloorHour(DateTime.SpecifyKind(o, DateTimeKind.Utc)) : through;
        }

        for (var start = from; start < through; start += Chunk)
        {
            var end = start + Chunk < through ? start + Chunk : through;
            await RollChunkAsync(start, end, ct);
        }

        await PruneAsync(nowUtc, ct);
        await SetWatermarkAsync(through, ct);
    }

    private async Task RollChunkAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var computed = await CountSpansAsync(db, null, fromUtc, toUtc, ct);
        if (computed.Count == 0) return;

        var existing = await db.ActivityHourlyCounts.Where(a => a.HourUtc >= fromUtc && a.HourUtc < toUtc).ToListAsync(ct);
        var byKey = existing.ToDictionary(Key);

        foreach (var cameraHour in computed.GroupBy(c => (c.CameraId, c.HourUtc)))
        {
            // AI rows for this camera-hour now: its own classifications are superseded.
            if (cameraHour.Any(c => c.Source == ActivitySource.Ai))
            {
                var stale = existing.Where(e => e.CameraId == cameraHour.Key.CameraId && e.HourUtc == cameraHour.Key.HourUtc
                    && e.Source == ActivitySource.Camera).ToList();
                db.ActivityHourlyCounts.RemoveRange(stale);
                foreach (var s in stale) byKey.Remove(Key(s));
            }

            foreach (var row in cameraHour)
            {
                if (byKey.TryGetValue(Key(row), out var current))
                {
                    if (row.Count > current.Count) current.Count = row.Count;
                }
                else
                {
                    db.ActivityHourlyCounts.Add(row);
                    byKey[Key(row)] = row;
                }
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static (Guid, DateTime, ActivitySource, string, string?) Key(ActivityHourlyCount a) =>
        (a.CameraId, a.HourUtc, a.Source, a.Group, a.Label);

    /// <summary>Events per camera, UTC hour, source, type and label from MotionSpan, with the
    /// AI-over-camera rule applied per camera-hour. Shared with the heatmap query's live tail.</summary>
    internal static async Task<List<ActivityHourlyCount>> CountSpansAsync(ApplicationDbContext db, IReadOnlyCollection<Guid>? cameraIds,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var query = db.MotionSpans.AsNoTracking().Where(m => m.StartUtc >= fromUtc && m.StartUtc < toUtc);
        if (cameraIds is not null) query = query.Where(m => cameraIds.Contains(m.CameraId));

        // Grouped in SQL by UTC date + hour, so only counts leave the database.
        var rows = await query
            .Select(m => new
            {
                m.CameraId,
                Day = m.StartUtc.Date,
                Hour = m.StartUtc.Hour,
                IsTag = m.EventTagRuleId != null,
                Rule = m.EventTagRule != null ? m.EventTagRule.Name : null,
                Category = m.DetectedObjectCategory != null ? m.DetectedObjectCategory.Name : null,
                m.DetectedObjectLabel,
                m.DetectionKind,
            })
            .GroupBy(x => new { x.CameraId, x.Day, x.Hour, x.IsTag, x.Rule, x.Category, x.DetectedObjectLabel, x.DetectionKind })
            .Select(g => new { g.Key.CameraId, g.Key.Day, g.Key.Hour, g.Key.IsTag, g.Key.Rule, g.Key.Category, g.Key.DetectedObjectLabel, g.Key.DetectionKind, Count = g.Count() })
            .ToListAsync(ct);

        var aiCameraHours = rows.Where(r => r.Category is not null && !r.IsTag)
            .Select(r => (r.CameraId, r.Day, r.Hour)).ToHashSet();

        var result = new List<ActivityHourlyCount>();
        foreach (var r in rows)
        {
            var hourUtc = DateTime.SpecifyKind(r.Day, DateTimeKind.Utc).AddHours(r.Hour);
            ActivityHourlyCount Row(ActivitySource source, string group, string? label) =>
                new() { CameraId = r.CameraId, HourUtc = hourUtc, Source = source, Group = group, Label = label, Count = r.Count };

            if (r.IsTag) result.Add(Row(ActivitySource.Tag, ActivityHeatmapBuilder.TagsGroup, r.Rule ?? "(deleted tag)"));
            else if (r.Category is not null) result.Add(Row(ActivitySource.Ai, r.Category, r.DetectedObjectLabel));
            else if (r.DetectionKind is { } kind)
            {
                if (!aiCameraHours.Contains((r.CameraId, r.Day, r.Hour)))
                    result.Add(Row(ActivitySource.Camera, ActivityService.GroupOf(kind), DetectionDisplay.Label(kind)));
            }
            else result.Add(Row(ActivitySource.Motion, ActivityHeatmapBuilder.MotionGroup, null));
        }

        // Two raw rows can fold into one key (e.g. Human + Face both into "Human"): sum them.
        return result
            .GroupBy(Key)
            .Select(g => { var first = g.First(); first.Count = g.Sum(x => x.Count); return first; })
            .ToList();
    }

    private async Task PruneAsync(DateTime nowUtc, CancellationToken ct)
    {
        var raw = await db.Settings.AsNoTracking().Where(s => s.Key == HistoryDaysKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var days = int.TryParse(raw, out var d) && d > 0 ? d : DefaultHistoryDays;
        var cutoff = nowUtc.AddDays(-days);
        var old = await db.ActivityHourlyCounts.Where(a => a.HourUtc < cutoff).Take(50_000).ToListAsync(ct);
        if (old.Count == 0) return;
        db.ActivityHourlyCounts.RemoveRange(old);
        await db.SaveChangesAsync(ct);
    }

    private async Task SetWatermarkAsync(DateTime through, CancellationToken ct)
    {
        var value = through.ToString("o", CultureInfo.InvariantCulture);
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == WatermarkKey, ct);
        if (setting is null)
            db.Settings.Add(new Setting { Id = Guid.NewGuid(), Key = WatermarkKey, Value = value, CreatedAt = DateTime.UtcNow, LastModifiedAt = DateTime.UtcNow, LastModifiedBy = "system" });
        else
        {
            setting.Value = value;
            setting.LastModifiedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
