using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Helpers;

namespace LarisVMS.Tests;

/// <summary>The camera map: placements (normalised, narrowed to the viewer's cameras, coloured by
/// status), map kinds, tile settings, and the floorplan upload's signature check.</summary>
public class MapServiceTests
{
    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Camera NewCamera(ApplicationDbContext db, string name, bool enabled = true, DateTime? health = null)
    {
        var c = new Camera
        {
            Id = Guid.NewGuid(), Name = name, Host = "h", DeviceServiceUri = "http://h/onvif/device_service",
            IsEnabled = enabled, NodeId = Guid.NewGuid(),
        };
        db.Add(c);
        db.Add(new CameraStream { Id = Guid.NewGuid(), CameraId = c.Id, Role = CameraStreamRole.Main, HealthReportedAt = health });
        return c;
    }

    private static MapService Service(ApplicationDbContext db) => new(db, new SettingsResolver(db));

    [Fact]
    public async Task PlacementIsNormalisedAndUpdatedInPlace()
    {
        using var db = NewDb();
        var cam = NewCamera(db, "Deck");
        await db.SaveChangesAsync();
        var svc = Service(db);
        var map = await svc.CreateAsync(new CreateSiteMapRequest("Yard", "Street", 45, -122, 18, null, null, null, null), null);

        Assert.True(await svc.SavePlacementAsync(map.Id, cam.Id, new SavePlacementRequest(45.1, -122.1, -90, 500, 0)));
        Assert.True(await svc.SavePlacementAsync(map.Id, cam.Id, new SavePlacementRequest(45.2, -122.2, 370, 2, 25)));

        var p = Assert.Single(db.CameraPlacements);
        Assert.Equal(45.2, p.Y);
        Assert.Equal(10, p.BearingDeg, 6);
        Assert.Equal(5, p.FovDeg);
        Assert.Equal(25, p.Range);
    }

    [Fact]
    public async Task PlacementNeedsAnExistingMapAndCamera()
    {
        using var db = NewDb();
        var cam = NewCamera(db, "Deck");
        await db.SaveChangesAsync();
        var svc = Service(db);
        var map = await svc.CreateAsync(new CreateSiteMapRequest("Yard", "Street", null, null, null, null, null, null, null), null);
        var req = new SavePlacementRequest(1, 1, 0, 90, 30);

        Assert.False(await svc.SavePlacementAsync(Guid.NewGuid(), cam.Id, req));
        Assert.False(await svc.SavePlacementAsync(map.Id, Guid.NewGuid(), req));
        Assert.False(await svc.RemovePlacementAsync(map.Id, cam.Id));
    }

    [Fact]
    public async Task MapShowsOnlyAccessibleCamerasWithTheirStatus()
    {
        using var db = NewDb();
        var live = NewCamera(db, "Live", health: DateTime.UtcNow.AddSeconds(-30));
        var stale = NewCamera(db, "Stale", health: DateTime.UtcNow.AddHours(-2));
        var off = NewCamera(db, "Off", enabled: false, health: DateTime.UtcNow);
        var hidden = NewCamera(db, "Hidden", health: DateTime.UtcNow);
        await db.SaveChangesAsync();
        var svc = Service(db);
        var map = await svc.CreateAsync(new CreateSiteMapRequest("Yard", "Street", null, null, null, null, null, null, null), null);
        foreach (var c in new[] { live, stale, off, hidden })
            await svc.SavePlacementAsync(map.Id, c.Id, new SavePlacementRequest(1, 1, 0, 90, 30));

        var accessible = new HashSet<Guid> { live.Id, stale.Id, off.Id };
        var dto = await svc.GetAsync(map.Id, accessible);

        Assert.NotNull(dto);
        Assert.Equal(["Live", "Off", "Stale"], dto.Placements.Select(p => p.CameraName));
        Assert.Equal(["recording", "disabled", "offline"], dto.Placements.Select(p => p.Status));
        Assert.Equal(3, Assert.Single(await svc.ListAsync(accessible)).CameraCount);
        Assert.Equal(4, Assert.Single(await svc.ListAsync(null)).CameraCount);
    }

    [Fact]
    public async Task AnOutOfRangeStreetViewIsIgnored()
    {
        using var db = NewDb();
        var svc = Service(db);
        var map = await svc.CreateAsync(new CreateSiteMapRequest("Yard", "Street", 45, -122, 18, null, null, null, null), null);

        Assert.True(await svc.UpdateAsync(map.Id, new UpdateSiteMapRequest(null, 95, -122, 18)));
        Assert.True(await svc.UpdateAsync(map.Id, new UpdateSiteMapRequest(null, 45, -122, 40)));
        var dto = await svc.GetAsync(map.Id, null);
        Assert.Equal(45, dto!.CenterLat);
        Assert.Equal(18, dto.Zoom);
    }

    [Fact]
    public async Task FloorplanKeepsItsImageAndSavesItsView()
    {
        using var db = NewDb();
        var svc = Service(db);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2 };
        var map = await svc.CreateAsync(new CreateSiteMapRequest("Shop", "floorplan", 45, -122, 10, "x", "image/png", 800, 600), png);

        Assert.Equal(SiteMapKind.Floorplan, map.Kind);
        Assert.Null(map.CenterLat);
        Assert.True(await svc.UpdateAsync(map.Id, new UpdateSiteMapRequest("Shop floor", 1, 2, 3)));

        var dto = await svc.GetAsync(map.Id, null);
        Assert.Equal("Shop floor", dto!.Name);
        Assert.Equal(1, dto.CenterLat); // image coordinates, not a street position
        Assert.Equal(3, dto.Zoom);
        Assert.Equal(800, dto.ImageWidth);
        var image = await svc.GetImageAsync(map.Id);
        Assert.Equal("image/png", image!.Value.ContentType);
        Assert.Equal(png, image.Value.Image);
    }

    [Fact]
    public async Task DeletingAMapRemovesItsPlacementsButNotTheCameras()
    {
        using var db = NewDb();
        var cam = NewCamera(db, "Deck");
        await db.SaveChangesAsync();
        var svc = Service(db);
        var map = await svc.CreateAsync(new CreateSiteMapRequest("Yard", "Street", null, null, null, null, null, null, null), null);
        await svc.SavePlacementAsync(map.Id, cam.Id, new SavePlacementRequest(1, 1, 0, 90, 30));

        Assert.True(await svc.DeleteAsync(map.Id));

        Assert.Empty(db.SiteMaps);
        Assert.Empty(db.CameraPlacements);
        Assert.Single(db.Cameras);
    }

    [Fact]
    public async Task TileSettingsDefaultToOpenStreetMapAndPersist()
    {
        using var db = NewDb();
        var svc = Service(db);
        Assert.Equal(MapService.DefaultTileUrl, (await svc.GetTileSettingsAsync()).TileUrl);

        await svc.SaveTileSettingsAsync(new MapTileSettingsDto(" https://tiles.lan/{z}/{x}/{y}.png ", "Local"), "admin");

        var saved = await Service(db).GetTileSettingsAsync();
        Assert.Equal("https://tiles.lan/{z}/{x}/{y}.png", saved.TileUrl);
        Assert.Equal("Local", saved.Attribution);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0 }, "image/gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67, 0x20 }, null)] // "<svg " — never accepted
    [InlineData(new byte[] { 0x89, 0x50 }, null)]
    public void FloorplanUploadIsRecognisedBySignature(byte[] data, string? expected) =>
        Assert.Equal(expected, SiteMapImage.DetectContentType(data));
}
