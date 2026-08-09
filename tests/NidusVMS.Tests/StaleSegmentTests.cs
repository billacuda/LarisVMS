using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Enums;
using NidusVMS.Infrastructure.Data;
using NidusVMS.Infrastructure.Services;

namespace NidusVMS.Tests;

/// <summary>
/// Covers the Cameras/Index and Admin/Nodes stale-footage warning: a camera reassigned from one
/// node to another still has Segment rows recorded under the old node's Id. This is a live query,
/// not a stored flag, so it must reflect exactly what's in Segments right now — present while the
/// old rows exist, gone the moment they don't.
/// </summary>
public class StaleSegmentTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task CameraReassignedAwayFromANodeWithOldSegmentsIsFlaggedStale()
    {
        var db = NewDb();
        var oldNode = new NidusVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-a", ApiKeyHash = "hash" };
        var newNode = new NidusVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-b", ApiKeyHash = "hash" };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = newNode.Id
        };
        db.AddRange(oldNode, newNode, camera);
        db.Segments.Add(new Segment
        {
            CameraId = camera.Id, NodeId = oldNode.Id, StreamRole = CameraStreamRole.Main,
            StartUtc = DateTime.UtcNow.AddMinutes(-1), EndUtc = DateTime.UtcNow, FilePath = "a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new CameraService(db, () => new HttpClient());
        var stale = await service.GetStaleSegmentNodeIdsAsync();

        Assert.True(stale.ContainsKey(camera.Id));
        Assert.Equal([oldNode.Id], stale[camera.Id]);
    }

    [Fact]
    public async Task CameraWhoseSegmentsAreAllOnItsCurrentNodeIsNotFlagged()
    {
        var db = NewDb();
        var node = new NidusVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-a", ApiKeyHash = "hash" };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id
        };
        db.AddRange(node, camera);
        db.Segments.Add(new Segment
        {
            CameraId = camera.Id, NodeId = node.Id, StreamRole = CameraStreamRole.Main,
            StartUtc = DateTime.UtcNow.AddMinutes(-1), EndUtc = DateTime.UtcNow, FilePath = "a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new CameraService(db, () => new HttpClient());
        var stale = await service.GetStaleSegmentNodeIdsAsync();

        Assert.False(stale.ContainsKey(camera.Id));
    }

    [Fact]
    public async Task WarningClearsOnceTheStaleSegmentsAreGone()
    {
        var db = NewDb();
        var oldNode = new NidusVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-a", ApiKeyHash = "hash" };
        var newNode = new NidusVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-b", ApiKeyHash = "hash" };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = newNode.Id
        };
        var segment = new Segment
        {
            CameraId = camera.Id, NodeId = oldNode.Id, StreamRole = CameraStreamRole.Main,
            StartUtc = DateTime.UtcNow.AddMinutes(-1), EndUtc = DateTime.UtcNow, FilePath = "a.mp4"
        };
        db.AddRange(oldNode, newNode, camera);
        db.Segments.Add(segment);
        await db.SaveChangesAsync();

        var service = new CameraService(db, () => new HttpClient());
        Assert.True((await service.GetStaleSegmentNodeIdsAsync()).ContainsKey(camera.Id));

        db.Segments.Remove(segment);
        await db.SaveChangesAsync();

        Assert.False((await service.GetStaleSegmentNodeIdsAsync()).ContainsKey(camera.Id));
    }
}
