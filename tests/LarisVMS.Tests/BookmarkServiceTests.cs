using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

public class BookmarkServiceTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task CreateAsyncPersistsAndReturnsTheBookmark()
    {
        using var db = NewDb();
        var service = new BookmarkService(db);
        var cameraId = Guid.NewGuid();
        var at = DateTime.UtcNow;

        var bookmark = await service.CreateAsync(cameraId, at, "something happened", "user-1", "Alice");

        Assert.Equal(cameraId, bookmark.CameraId);
        Assert.Equal(1, await db.Bookmarks.CountAsync());
    }

    [Fact]
    public async Task ListAsyncOrdersNewestFirstAndFallsBackForADeletedCamera()
    {
        using var db = NewDb();
        var liveCamera = new Camera { Id = Guid.NewGuid(), Name = "Front Door", Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif/device_service" };
        db.Cameras.Add(liveCamera);

        var now = DateTime.UtcNow;
        db.Bookmarks.AddRange(
            new Bookmark { Id = Guid.NewGuid(), CameraId = liveCamera.Id, TimestampUtc = now.AddMinutes(-10), Note = "older", CreatedByUserId = "u" },
            new Bookmark { Id = Guid.NewGuid(), CameraId = Guid.NewGuid(), TimestampUtc = now, Note = "newer, camera since removed", CreatedByUserId = "u" });
        await db.SaveChangesAsync();

        var service = new BookmarkService(db);
        var list = await service.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("newer, camera since removed", list[0].Note);
        Assert.Equal("(deleted camera)", list[0].CameraName);
        Assert.Equal("Front Door", list[1].CameraName);
    }

    // DeleteAsync isn't covered here: it uses ExecuteDeleteAsync (same convention as
    // CameraService/ZoneService/etc.), which EF Core's InMemory provider — what these tests run
    // against — throws InvalidOperationException on unconditionally. None of this codebase's other
    // ExecuteDeleteAsync-based delete methods are unit-tested for the same reason.

    [Fact]
    public async Task ListForCameraAsyncReturnsOnlyThatCamerasBookmarksInRangeOldestFirst()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var otherCameraId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.Bookmarks.AddRange(
            new Bookmark { Id = Guid.NewGuid(), CameraId = cameraId, TimestampUtc = now.AddMinutes(-5), Note = "later", CreatedByUserId = "u" },
            new Bookmark { Id = Guid.NewGuid(), CameraId = cameraId, TimestampUtc = now.AddMinutes(-10), Note = "earlier", CreatedByUserId = "u" },
            new Bookmark { Id = Guid.NewGuid(), CameraId = otherCameraId, TimestampUtc = now.AddMinutes(-7), Note = "other camera", CreatedByUserId = "u" },
            new Bookmark { Id = Guid.NewGuid(), CameraId = cameraId, TimestampUtc = now.AddDays(-2), Note = "out of range", CreatedByUserId = "u" });
        await db.SaveChangesAsync();

        var service = new BookmarkService(db);
        var markers = await service.ListForCameraAsync(cameraId, now.AddHours(-1), now, CancellationToken.None);

        Assert.Equal(2, markers.Count);
        Assert.Equal("earlier", markers[0].Note);
        Assert.Equal("later", markers[1].Note);
    }
}
