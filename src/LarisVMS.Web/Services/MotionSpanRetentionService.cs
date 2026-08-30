using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 2c: deletes a MotionSpan once the footage it points
/// at has aged out of retention — a card that can't be played back is noise, and pass 3's later eager
/// snapshot capture would otherwise leave a perfectly valid-looking image file with nothing behind it
/// to play. Mirrors BookmarkRetentionService's own "entries expire with the footage they point at"
/// pattern almost exactly — same 6-hour cadence, same scope-per-tick shape, same staleness rule (a
/// row is stale once its camera's earliest remaining Segment starts after the row's own timestamp, or
/// immediately if the camera has no segments left at all) — with two differences MotionSpans needs
/// that Bookmarks didn't:
///
/// 1. Compared against <c>StartUtc - the camera's own pre-roll</c> (the same instant
///    TimelineService.GetSnapshotsAsync now exposes as SnapshotDto.PlayFromUtc — pass 2b), not
///    StartUtc itself, since the pre-roll lead-in is part of what a snapshot card promises to play.
/// 2. Batched via keyset pagination on Id, not loaded whole — MotionSpans is far higher-volume than
///    Bookmarks (~16,600 rows was normal historically), so unlike the bookmark sweep this must never
///    materialize the entire table. Keyset (not repeated Take(N)) because it must keep advancing past
///    rows that were *not* deleted this batch — a naive re-query of the same page would loop forever
///    re-fetching survivors.
///
/// GetSnapshotsAsync's own query-time guard (pass 2c) hides an aged-out span from the page
/// immediately; this sweep is purely for reclaiming the row (and, via the node-side reconciliation
/// sweep in StorageManager, the cached crop file) once it's no longer needed for anything.
/// </summary>
public class MotionSpanRetentionService(IServiceScopeFactory scopeFactory, ILogger<MotionSpanRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    internal const int BatchSize = 1000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
                await SweepAsync(db, settings, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "MotionSpanRetentionService sweep failed — will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>internal, not private: unit-tested directly against an InMemory ApplicationDbContext
    /// (MotionSpanRetentionServiceTests), same reasoning as BookmarkRetentionService.SweepAsync's own
    /// doc comment. settings may be null (as in most existing TimelineService tests) — resolves every
    /// camera's pre-roll to the same 10s default TimelineService itself falls back to.</summary>
    internal static async Task SweepAsync(ApplicationDbContext db, ISettingsResolver? settings, CancellationToken ct)
    {
        var camerasWithSpans = await db.MotionSpans.Select(m => m.CameraId).Distinct().ToListAsync(ct);
        if (camerasWithSpans.Count == 0) return;

        var earliestSegmentByCamera = await db.Segments
            .Where(s => camerasWithSpans.Contains(s.CameraId))
            .GroupBy(s => s.CameraId)
            .Select(g => new { CameraId = g.Key, EarliestStartUtc = g.Min(s => s.StartUtc) })
            .ToDictionaryAsync(x => x.CameraId, x => x.EarliestStartUtc, ct);

        var preRollByCameraId = new Dictionary<Guid, int>();

        long lastId = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = await db.MotionSpans.Where(m => m.Id > lastId)
                .OrderBy(m => m.Id).Take(BatchSize).ToListAsync(ct);
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            foreach (var camId in batch.Select(m => m.CameraId).Distinct())
            {
                if (preRollByCameraId.ContainsKey(camId)) continue;
                preRollByCameraId[camId] = settings is not null
                    ? await settings.GetAsync("Recording.MotionPreRollSeconds", 10, cameraId: camId, ct: ct)
                    : 10;
            }

            var stale = batch.Where(m =>
            {
                if (!earliestSegmentByCamera.TryGetValue(m.CameraId, out var earliest)) return true;
                var preRoll = preRollByCameraId.GetValueOrDefault(m.CameraId, 10);
                var playFromUtc = m.StartUtc - TimeSpan.FromSeconds(preRoll);
                return playFromUtc < earliest;
            }).ToList();

            if (stale.Count > 0)
            {
                db.MotionSpans.RemoveRange(stale);
                await db.SaveChangesAsync(ct);
            }

            if (batch.Count < BatchSize) break;
        }
    }
}
