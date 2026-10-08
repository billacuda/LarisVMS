using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// M18: deletes a Bookmark once the footage it points at has aged out of retention — "entries expire
/// with the footage they point at" is the feature's own explicit requirement, not an incidental
/// cleanup. A bookmark is stale once its camera's earliest remaining Segment starts after the
/// bookmark's own timestamp (retention deletes oldest-first, so that's proof the covering footage is
/// gone), or immediately if the camera has no segments left at all. Same 6-hour cadence and
/// scope-per-tick shape as AuditLogRetentionService — this app's other "sweep something whose
/// lifetime is tied to a different table's own retention" background service.
/// </summary>
public class BookmarkRetentionService(IServiceScopeFactory scopeFactory, ILogger<BookmarkRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await SweepAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "BookmarkRetentionService sweep failed — will retry next cycle.");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>internal, not private: unit-tested directly against an InMemory ApplicationDbContext
    /// (BookmarkRetentionServiceTests), same reasoning as AuditLogRetentionService.SweepAsync's own
    /// doc comment for why a real-DbContext test earns its keep here over a pure-logic-only one.</summary>
    internal static async Task SweepAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var bookmarkCameraIds = await db.Bookmarks.Select(b => b.CameraId).Distinct().ToListAsync(ct);
        if (bookmarkCameraIds.Count == 0) return;

        var earliestSegmentByCamera = await db.Segments
            .Where(s => bookmarkCameraIds.Contains(s.CameraId))
            .GroupBy(s => s.CameraId)
            .Select(g => new { CameraId = g.Key, EarliestStartUtc = g.Min(s => s.StartUtc) })
            .ToDictionaryAsync(x => x.CameraId, x => x.EarliestStartUtc, ct);

        var bookmarks = await db.Bookmarks.ToListAsync(ct);
        var stale = bookmarks.Where(b =>
            !earliestSegmentByCamera.TryGetValue(b.CameraId, out var earliest) || b.TimestampUtc < earliest
        ).ToList();

        if (stale.Count == 0) return;
        db.Bookmarks.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
    }
}
