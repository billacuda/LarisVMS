using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// "Entries expire with the footage they point at" is the feature's own explicit requirement — a
/// bookmark is stale once its camera's earliest remaining Segment starts after the bookmark's own
/// timestamp, or immediately if the camera has no segments left at all. Mirrors
/// AuditLogRetentionServiceTests' shape: a real InMemory ApplicationDbContext, not a pure-logic test,
/// since the interesting behavior is entirely in the join/comparison against Segments.
/// </summary>
public class BookmarkRetentionServiceTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static Bookmark MakeBookmark(Guid cameraId, DateTime timestampUtc) => new()
    {
        Id = Guid.NewGuid(),
        CameraId = cameraId,
        TimestampUtc = timestampUtc,
        Note = "test",
        CreatedByUserId = "user-1"
    };

    private static Segment MakeSegment(Guid cameraId, DateTime startUtc) => new()
    {
        CameraId = cameraId,
        NodeId = Guid.NewGuid(),
        StreamRole = CameraStreamRole.Main,
        StartUtc = startUtc,
        EndUtc = startUtc.AddMinutes(1),
        FilePath = "a.mp4"
    };

    [Fact]
    public async Task BookmarkNewerThanEarliestSegmentSurvives()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Segments.Add(MakeSegment(cameraId, now.AddDays(-10)));
        db.Bookmarks.Add(MakeBookmark(cameraId, now.AddDays(-5)));
        await db.SaveChangesAsync();

        await BookmarkRetentionService.SweepAsync(db, CancellationToken.None);

        Assert.Equal(1, await db.Bookmarks.CountAsync());
    }

    [Fact]
    public async Task BookmarkOlderThanEarliestRemainingSegmentIsDeleted()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        // Footage has already retention-aged past the bookmark's own instant.
        db.Segments.Add(MakeSegment(cameraId, now.AddDays(-5)));
        db.Bookmarks.Add(MakeBookmark(cameraId, now.AddDays(-10)));
        await db.SaveChangesAsync();

        await BookmarkRetentionService.SweepAsync(db, CancellationToken.None);

        Assert.Equal(0, await db.Bookmarks.CountAsync());
    }

    [Fact]
    public async Task BookmarkForCameraWithNoSegmentsAtAllIsDeleted()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        db.Bookmarks.Add(MakeBookmark(cameraId, DateTime.UtcNow.AddDays(-1)));
        await db.SaveChangesAsync();

        await BookmarkRetentionService.SweepAsync(db, CancellationToken.None);

        Assert.Equal(0, await db.Bookmarks.CountAsync());
    }

    [Fact]
    public async Task OnlyTheStaleBookmarkIsRemovedWhenMultipleCamerasAreMixed()
    {
        using var db = NewDb();
        var freshCamera = Guid.NewGuid();
        var staleCamera = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Segments.Add(MakeSegment(freshCamera, now.AddDays(-30)));
        // staleCamera has no segments at all — its bookmark should go.
        db.Bookmarks.AddRange(
            MakeBookmark(freshCamera, now.AddDays(-1)),
            MakeBookmark(staleCamera, now.AddDays(-1)));
        await db.SaveChangesAsync();

        await BookmarkRetentionService.SweepAsync(db, CancellationToken.None);

        var remaining = await db.Bookmarks.ToListAsync();
        Assert.Single(remaining);
        Assert.Equal(freshCamera, remaining[0].CameraId);
    }

    [Fact]
    public async Task NoBookmarksIsANoOp()
    {
        using var db = NewDb();

        await BookmarkRetentionService.SweepAsync(db, CancellationToken.None);

        Assert.Equal(0, await db.Bookmarks.CountAsync());
    }
}
