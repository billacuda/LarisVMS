using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Enums;
using NidusVMS.Infrastructure.Data;
using NidusVMS.Infrastructure.Services;

namespace NidusVMS.Tests;

/// <summary>
/// Covers the M4 resolution order — Camera → Node → Global → caller default — added for the
/// per-camera/per-node retention override. Each test uses fresh Guids so the resolver's static
/// cache (shared process-wide by design, invalidated only in bulk) can't leak state between tests.
/// </summary>
public class SettingsResolverTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<(ApplicationDbContext Db, Guid CameraId, Guid NodeId)> SeedAsync()
    {
        var db = NewDb();
        var node = new NidusVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash" };
        var camera = new Camera { Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id };
        db.Nodes.Add(node);
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();
        return (db, camera.Id, node.Id);
    }

    [Fact]
    public async Task FallsThroughToGlobalWhenNoOverrideExists()
    {
        var (db, cameraId, nodeId) = await SeedAsync();
        var resolver = new SettingsResolver(db);
        await resolver.SetGlobalAsync("Retention.Days", "45");

        var value = await resolver.GetAsync("Retention.Days", 30, cameraId, nodeId);

        Assert.Equal(45, value);
        Assert.Equal(SettingScope.Global, await resolver.GetSourceAsync("Retention.Days", cameraId, nodeId));
    }

    [Fact]
    public async Task NodeOverrideWinsOverGlobal()
    {
        var (db, cameraId, nodeId) = await SeedAsync();
        var resolver = new SettingsResolver(db);
        await resolver.SetGlobalAsync("Retention.Days", "45");
        await resolver.SetOverrideAsync(SettingScope.Node, nodeId, "Retention.Days", "20");

        var value = await resolver.GetAsync("Retention.Days", 30, cameraId, nodeId);

        Assert.Equal(20, value);
        Assert.Equal(SettingScope.Node, await resolver.GetSourceAsync("Retention.Days", cameraId, nodeId));
    }

    [Fact]
    public async Task CameraOverrideWinsOverNodeAndGlobal()
    {
        var (db, cameraId, nodeId) = await SeedAsync();
        var resolver = new SettingsResolver(db);
        await resolver.SetGlobalAsync("Retention.Days", "45");
        await resolver.SetOverrideAsync(SettingScope.Node, nodeId, "Retention.Days", "20");
        await resolver.SetOverrideAsync(SettingScope.Camera, cameraId, "Retention.Days", "7");

        var value = await resolver.GetAsync("Retention.Days", 30, cameraId, nodeId);

        Assert.Equal(7, value);
        Assert.Equal(SettingScope.Camera, await resolver.GetSourceAsync("Retention.Days", cameraId, nodeId));
    }

    [Fact]
    public async Task CameraWithNoExplicitNodeIdStillFallsThroughItsOwnNodesOverride()
    {
        // Mirrors how NodeService.GetConfigAsync resolves retention: the caller passes the camera's
        // own NodeId explicitly, but ISettingsResolver can also look it up itself when omitted.
        var (db, cameraId, nodeId) = await SeedAsync();
        var resolver = new SettingsResolver(db);
        await resolver.SetOverrideAsync(SettingScope.Node, nodeId, "Retention.Days", "20");

        var value = await resolver.GetAsync<int?>("Retention.Days", null, cameraId);

        Assert.Equal(20, value);
    }

    [Fact]
    public async Task ClearingAnOverrideFallsBackToTheNextScope()
    {
        var (db, cameraId, nodeId) = await SeedAsync();
        var resolver = new SettingsResolver(db);
        await resolver.SetGlobalAsync("Retention.Days", "45");
        await resolver.SetOverrideAsync(SettingScope.Camera, cameraId, "Retention.Days", "7");
        Assert.Equal(7, await resolver.GetAsync("Retention.Days", 30, cameraId, nodeId));

        await resolver.SetOverrideAsync(SettingScope.Camera, cameraId, "Retention.Days", null);

        Assert.Equal(45, await resolver.GetAsync("Retention.Days", 30, cameraId, nodeId));
        Assert.Null(await resolver.GetOwnOverrideAsync(SettingScope.Camera, cameraId, "Retention.Days"));
    }
}
