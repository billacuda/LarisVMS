using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

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

    /// <summary>How long raw camera events (the ONVIF event log behind event-tag rules' topic list)
    /// are kept. 0 keeps them forever.</summary>
    public const string CameraEventRetentionDaysKey = "Events.RawRetentionDays";
    public const int DefaultCameraEventRetentionDays = 30;
    internal const int CameraEventBatchSize = 5000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
                // Count every span into the activity history before any of them can be deleted —
                // a small-quota camera's spans can go within hours of being recorded.
                await scope.ServiceProvider.GetRequiredService<ActivityRollupService>().RollupAsync(DateTime.UtcNow, stoppingToken);
                // Same for speed-trap measurements, which go with their spans.
                await scope.ServiceProvider.GetRequiredService<SpeedRollupService>().RollupAsync(DateTime.UtcNow, stoppingToken);
                // And object paths, for the scene heatmap.
                await scope.ServiceProvider.GetRequiredService<SceneHeatRollupService>().RollupAsync(DateTime.UtcNow, stoppingToken);
                await SweepAsync(db, settings, stoppingToken);
                await SweepTalkRecordingsAsync(db, stoppingToken);
                var eventDays = await settings.GetAsync(CameraEventRetentionDaysKey, DefaultCameraEventRetentionDays, ct: stoppingToken);
                var removedEvents = await SweepCameraEventsAsync(db, eventDays, stoppingToken);
                if (removedEvents > 0)
                    logger.LogInformation("Removed {Count} camera event(s) older than {Days} days.", removedEvents, eventDays);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "MotionSpanRetentionService sweep failed — will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
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

        // Evidence locks: cut-off from the earliest unlocked footage; spans inside a lock are kept.
        var (earliestSegmentByCamera, locked) = await LarisVMS.Infrastructure.Services.EvidenceLockQueries
            .LoadRetentionCutoffsAsync(db, camerasWithSpans, DateTime.UtcNow, ct);

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
                if (locked.Covers(m.CameraId, m.StartUtc, m.EndUtc)) return false;
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

    /// <summary>Raw camera events older than <paramref name="retentionDays"/> (0 keeps them forever),
    /// camera by camera so each delete seeks the (CameraId, ReceivedUtc) clustered index, and in
    /// batches so a first run over months of events doesn't become one huge transaction. A set-based
    /// DELETE on SQL Server; the InMemory provider the tests use has no ExecuteDelete, so there it
    /// removes tracked stubs instead. Returns how many were removed.</summary>
    internal static async Task<int> SweepCameraEventsAsync(ApplicationDbContext db, int retentionDays, CancellationToken ct)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var cameras = await db.Cameras.AsNoTracking().Select(c => c.Id).ToListAsync(ct);
        var removed = 0;
        foreach (var cameraId in cameras)
        {
            while (!ct.IsCancellationRequested)
            {
                var ids = await db.CameraEvents.AsNoTracking()
                    .Where(e => e.CameraId == cameraId && e.ReceivedUtc < cutoff)
                    .OrderBy(e => e.ReceivedUtc).Select(e => e.Id).Take(CameraEventBatchSize).ToListAsync(ct);
                if (ids.Count == 0) break;
                if (db.Database.IsRelational())
                    await db.CameraEvents.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);
                else
                {
                    db.CameraEvents.RemoveRange(await db.CameraEvents.Where(e => ids.Contains(e.Id)).ToListAsync(ct));
                    await db.SaveChangesAsync(ct);
                }
                removed += ids.Count;
                if (ids.Count < CameraEventBatchSize) break;
            }
        }
        return removed;
    }

    /// <summary>Two-way audio recordings expire with the footage around them, by the same rule as
    /// motion spans: once the camera's earliest remaining segment starts after the press ended (or the
    /// camera has no footage left). The node deletes the WAV itself by the camera's retention.</summary>
    internal static async Task SweepTalkRecordingsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var cameras = await db.TalkRecordings.Select(t => t.CameraId).Distinct().ToListAsync(ct);
        if (cameras.Count == 0) return;

        var (earliestSegmentByCamera, locked) = await LarisVMS.Infrastructure.Services.EvidenceLockQueries
            .LoadRetentionCutoffsAsync(db, cameras, DateTime.UtcNow, ct);

        foreach (var cameraId in cameras)
        {
            var stale = earliestSegmentByCamera.TryGetValue(cameraId, out var earliest)
                ? db.TalkRecordings.Where(t => t.CameraId == cameraId && t.EndUtc < earliest)
                : db.TalkRecordings.Where(t => t.CameraId == cameraId);
            // Evidence locks: a talk recording inside locked footage is kept with it.
            db.TalkRecordings.RemoveRange((await stale.ToListAsync(ct)).Where(t => !locked.Covers(cameraId, t.StartUtc, t.EndUtc)));
        }
        await db.SaveChangesAsync(ct);
    }
}
