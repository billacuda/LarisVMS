using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Builds the speed heatmap's long-term history (SpeedHourlyStat) from speed-trap
/// measurements, one finished hour at a time — the speed counterpart of ActivityRollupService, with
/// the same watermark, 48-hour re-roll and retention.
///
/// A watermark (setting Speed.RolledThroughUtc) records the first hour not yet rolled up. The first
/// run starts from the oldest measurement still in the database. Re-rolling only replaces an hour's
/// row when it now holds more measurements: objects are only ever added (a speed is set once and
/// never cleared) and deleted by retention, so a smaller count means some have aged out, and what was
/// rolled up before stays.
///
/// Runs hourly (ActivityRollupBackgroundService) and at the start of every MotionSpan retention sweep,
/// since measurements are deleted with their spans.</summary>
public class SpeedRollupService(ApplicationDbContext db)
{
    public const string WatermarkKey = "Speed.RolledThroughUtc";
    private static readonly TimeSpan Chunk = TimeSpan.FromDays(7);

    public async Task<DateTime?> GetWatermarkAsync(CancellationToken ct = default)
    {
        var raw = await db.Settings.AsNoTracking().Where(s => s.Key == WatermarkKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var w)
            ? DateTime.SpecifyKind(w, DateTimeKind.Utc) : null;
    }

    public async Task RollupAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var through = ActivityRollupService.FloorHour(nowUtc);
        var watermark = await GetWatermarkAsync(ct);
        DateTime from;
        if (watermark is { } w) from = w - ActivityRollupService.ReRollWindow;
        else
        {
            var oldest = await db.DetectedObjects.AsNoTracking()
                .Where(o => o.SpeedKph != null && o.SpeedMeasuredAtUtc != null)
                .OrderBy(o => o.SpeedMeasuredAtUtc).Select(o => o.SpeedMeasuredAtUtc).FirstOrDefaultAsync(ct);
            from = oldest is { } o ? ActivityRollupService.FloorHour(DateTime.SpecifyKind(o, DateTimeKind.Utc)) : through;
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
        var computed = await AggregateAsync(db, null, fromUtc, toUtc, ct);
        if (computed.Count == 0) return;

        var existing = await db.SpeedHourlyStats.Where(s => s.HourUtc >= fromUtc && s.HourUtc < toUtc).ToListAsync(ct);
        var byKey = existing.ToDictionary(Key);
        foreach (var row in computed)
        {
            if (byKey.TryGetValue(Key(row), out var current))
            {
                if (row.Count <= current.Count) continue;
                current.Count = row.Count;
                current.SumKph = row.SumKph;
                current.MaxKph = Math.Max(current.MaxKph, row.MaxKph);
                current.OverLimitCount = Math.Max(current.OverLimitCount, row.OverLimitCount);
            }
            else
            {
                db.SpeedHourlyStats.Add(row);
                byKey[Key(row)] = row;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static (Guid, DateTime, Guid?, string?) Key(SpeedHourlyStat s) => (s.CameraId, s.HourUtc, s.SpeedTrapId, s.Label);

    /// <summary>Measurements per camera, UTC hour, trap and label, aggregated in SQL. Shared with the
    /// heatmap's live tail (the hours since the watermark).</summary>
    internal static async Task<List<SpeedHourlyStat>> AggregateAsync(ApplicationDbContext db, IReadOnlyCollection<Guid>? cameraIds,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var objects = db.DetectedObjects.AsNoTracking()
            .Where(o => o.SpeedKph != null && o.SpeedMeasuredAtUtc >= fromUtc && o.SpeedMeasuredAtUtc < toUtc);
        if (cameraIds is not null) objects = objects.Where(o => cameraIds.Contains(o.CameraId));

        var rows = await (
                from o in objects
                from t in db.SpeedTraps.AsNoTracking().Where(t => t.Id == o.SpeedTrapId).DefaultIfEmpty()
                select new
                {
                    o.CameraId,
                    o.SpeedTrapId,
                    Day = o.SpeedMeasuredAtUtc!.Value.Date,
                    Hour = o.SpeedMeasuredAtUtc!.Value.Hour,
                    o.Label,
                    Kph = o.SpeedKph!.Value,
                    Over = t != null && t.SpeedLimitKph != null && o.SpeedKph > t.SpeedLimitKph ? 1 : 0,
                })
            .GroupBy(x => new { x.CameraId, x.SpeedTrapId, x.Day, x.Hour, x.Label })
            .Select(g => new
            {
                g.Key.CameraId, g.Key.SpeedTrapId, g.Key.Day, g.Key.Hour, g.Key.Label,
                Count = g.Count(), Sum = g.Sum(x => x.Kph), Max = g.Max(x => x.Kph), Over = g.Sum(x => x.Over),
            })
            .ToListAsync(ct);

        return rows.Select(r => new SpeedHourlyStat
        {
            CameraId = r.CameraId,
            SpeedTrapId = r.SpeedTrapId,
            HourUtc = DateTime.SpecifyKind(r.Day, DateTimeKind.Utc).AddHours(r.Hour),
            Label = r.Label,
            Count = r.Count,
            SumKph = r.Sum,
            MaxKph = r.Max,
            OverLimitCount = r.Over,
        }).ToList();
    }

    private async Task PruneAsync(DateTime nowUtc, CancellationToken ct)
    {
        var raw = await db.Settings.AsNoTracking().Where(s => s.Key == ActivityRollupService.HistoryDaysKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var days = int.TryParse(raw, out var d) && d > 0 ? d : ActivityRollupService.DefaultHistoryDays;
        var cutoff = nowUtc.AddDays(-days);
        var old = await db.SpeedHourlyStats.Where(s => s.HourUtc < cutoff).Take(50_000).ToListAsync(ct);
        if (old.Count == 0) return;
        db.SpeedHourlyStats.RemoveRange(old);
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
