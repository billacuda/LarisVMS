using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>GetCameraIdsInSubtreeAsync — the Snapshots page's "camera group" filter mode resolves a
/// selected group down to the camera IDs GetSnapshotsAsync needs via this, same subtree-by-
/// MaterializedPath-prefix cascade CameraAccessService already uses for a Group-scoped access
/// grant.</summary>
public class CameraGroupServiceTests
{
    private static (ApplicationDbContext Db, CameraGroupService Service) NewHarness()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        return (db, new CameraGroupService(db));
    }

    [Fact]
    public async Task ReturnsCamerasDirectlyInTheGroup()
    {
        var (db, service) = NewHarness();
        var group = await service.CreateAsync("Site A", null);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Front Door", Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif" };
        camera.Groups.Add(group);
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();

        var result = await service.GetCameraIdsInSubtreeAsync(group.Id);

        Assert.Equal([camera.Id], result);
    }

    [Fact]
    public async Task ReturnsCamerasBelongingToADescendantGroup()
    {
        var (db, service) = NewHarness();
        var site = await service.CreateAsync("Site A", null);
        var building = await service.CreateAsync("Building 2", site.Id);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Lobby", Host = "10.0.0.2", DeviceServiceUri = "http://10.0.0.2/onvif" };
        camera.Groups.Add(building);
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();

        var result = await service.GetCameraIdsInSubtreeAsync(site.Id);

        Assert.Equal([camera.Id], result);
    }

    [Fact]
    public async Task DoesNotReturnCamerasFromASiblingGroup()
    {
        var (db, service) = NewHarness();
        var siteA = await service.CreateAsync("Site A", null);
        var siteB = await service.CreateAsync("Site B", null);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Back Door", Host = "10.0.0.3", DeviceServiceUri = "http://10.0.0.3/onvif" };
        camera.Groups.Add(siteB);
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();

        var result = await service.GetCameraIdsInSubtreeAsync(siteA.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AGroupWithNoCamerasReturnsEmpty()
    {
        var (_, service) = NewHarness();
        var group = await service.CreateAsync("Empty Site", null);

        var result = await service.GetCameraIdsInSubtreeAsync(group.Id);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ANonexistentGroupReturnsEmpty()
    {
        var (_, service) = NewHarness();

        var result = await service.GetCameraIdsInSubtreeAsync(Guid.NewGuid());

        Assert.Empty(result);
    }
}
