using System.Globalization;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <summary>Builds the scene heatmap's objects history (SceneHeatHourly, Source Objects) from the
/// paths of detected objects, one finished hour at a time — same watermark, 48-hour re-roll, chunking
/// and retention as SpeedRollupService. Each object counts once in every grid cell its path crossed,
/// in the hour it was first seen, grouped by camera and object category.
///
/// Re-rolling only replaces an hour's row when it now holds more: objects are only added (and their
/// paths only grow) until retention deletes them, so a smaller total means some have aged out, and
/// what was rolled up before stays.
///
/// Runs hourly (ActivityRollupBackgroundService) and at the start of every MotionSpan retention sweep,
/// since paths are deleted with their spans.</summary>
public class SceneHeatRollupService(ApplicationDbContext db)
{
    public const string WatermarkKey = "SceneHeat.RolledThroughUtc";
    // A day at a time: paths are read and drawn in memory, so this bounds how many are held at once.
    private static readonly TimeSpan Chunk = TimeSpan.FromDays(1);

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
            var oldest = await db.DetectedObjects.AsNoTracking().Where(o => o.Path != null)
                .OrderBy(o => o.FirstSeenUtc).Select(o => (DateTime?)o.FirstSeenUtc).FirstOrDefaultAsync(ct);
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

        var existing = await db.SceneHeatHourlies
            .Where(s => s.Source == SceneHeatSource.Objects && s.HourUtc >= fromUtc && s.HourUtc < toUtc).ToListAsync(ct);
        var byKey = existing.ToDictionary(Key);
        foreach (var row in computed)
        {
            if (byKey.TryGetValue(Key(row), out var current))
            {
                if (Total(row) <= Total(current)) continue;
                current.Cols = row.Cols;
                current.Rows = row.Rows;
                current.Cells = row.Cells;
            }
            else
            {
                db.SceneHeatHourlies.Add(row);
                byKey[Key(row)] = row;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private static (Guid, DateTime, string) Key(SceneHeatHourly s) => (s.CameraId, s.HourUtc, s.Label);

    private static long Total(SceneHeatHourly s)
    {
        var cells = new long[s.Cols * s.Rows];
        SceneHeat.AddCells(s.Cells, cells);
        return cells.Sum();
    }

    /// <summary>Object heat per camera, UTC hour and category for objects first seen in
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>). Shared with the heatmap's live tail
    /// (the hours since the watermark).</summary>
    internal static async Task<List<SceneHeatHourly>> AggregateAsync(ApplicationDbContext db, IReadOnlyCollection<Guid>? cameraIds,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var objects = db.DetectedObjects.AsNoTracking()
            .Where(o => o.Path != null && o.FirstSeenUtc >= fromUtc && o.FirstSeenUtc < toUtc);
        if (cameraIds is not null) objects = objects.Where(o => cameraIds.Contains(o.CameraId));

        var grids = new Dictionary<(Guid Camera, DateTime Hour, string Category), long[]>();
        var shapes = new Dictionary<Guid, (int Cols, int Rows)>();
        await foreach (var o in objects.Select(o => new { o.CameraId, o.FirstSeenUtc, o.Label, o.Path }).AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!shapes.TryGetValue(o.CameraId, out var shape))
            {
                shape = await GridShapeAsync(db, o.CameraId, ct);
                shapes[o.CameraId] = shape;
            }
            var path = SceneHeat.ParsePath(o.Path);
            if (path.Count < 2) continue;

            var key = (o.CameraId, ActivityRollupService.FloorHour(DateTime.SpecifyKind(o.FirstSeenUtc, DateTimeKind.Utc)),
                CocoCategoryMap.Resolve(o.Label));
            if (!grids.TryGetValue(key, out var cells))
            {
                cells = new long[shape.Cols * shape.Rows];
                grids[key] = cells;
            }
            foreach (var cell in SceneHeat.PathCells(path, shape.Cols, shape.Rows)) cells[cell]++;
        }

        return grids.Select(g =>
        {
            var (cols, rows) = shapes[g.Key.Camera];
            return new SceneHeatHourly
            {
                CameraId = g.Key.Camera,
                HourUtc = g.Key.Hour,
                Source = SceneHeatSource.Objects,
                Label = g.Key.Category,
                Cols = cols,
                Rows = rows,
                Cells = SceneHeat.EncodeCells(g.Value.Select(c => (int)Math.Min(c, int.MaxValue)).ToArray()),
            };
        }).ToList();
    }

    /// <summary>A 64-column grid shaped like the camera's picture (its Main stream, else any stream with
    /// a known size).</summary>
    internal static async Task<(int Cols, int Rows)> GridShapeAsync(ApplicationDbContext db, Guid cameraId, CancellationToken ct)
    {
        var (width, height) = await CameraShapeAsync(db, cameraId, ct);
        return (SceneHeat.ObjectColumns, SceneHeat.RowsFor(width, height));
    }

    /// <summary>The camera's picture size in pixels (its Main stream, else any stream with a known
    /// size), or (0, 0) when no stream has reported one.</summary>
    public static async Task<(int Width, int Height)> CameraShapeAsync(ApplicationDbContext db, Guid cameraId, CancellationToken ct)
    {
        var size = await db.CameraStreams.AsNoTracking()
            .Where(s => s.CameraId == cameraId && s.Width != null && s.Height != null)
            .OrderBy(s => s.Role == CameraStreamRole.Main ? 0 : 1)
            .Select(s => new { s.Width, s.Height })
            .FirstOrDefaultAsync(ct);
        return (size?.Width ?? 0, size?.Height ?? 0);
    }

    private async Task PruneAsync(DateTime nowUtc, CancellationToken ct)
    {
        var raw = await db.Settings.AsNoTracking().Where(s => s.Key == ActivityRollupService.HistoryDaysKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var days = int.TryParse(raw, out var d) && d > 0 ? d : ActivityRollupService.DefaultHistoryDays;
        var cutoff = nowUtc.AddDays(-days);
        // Both sources: motion rows are reported by nodes, but kept for the same history.
        var old = await db.SceneHeatHourlies.Where(s => s.HourUtc < cutoff).Take(50_000).ToListAsync(ct);
        if (old.Count == 0) return;
        db.SceneHeatHourlies.RemoveRange(old);
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
