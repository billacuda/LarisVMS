using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Covers the wiring of AiDetection.Orientation from a Setting row through NodeService.GetConfigAsync
/// onto NodeConfigCameraDto. The Camera → Node → Global resolution itself is SettingsResolver's own
/// concern (see SettingsResolverTests); what is worth pinning here is that the resolved value
/// actually reaches the node, because NodeConfigCameraDto is a positional record with a long tail of
/// same-typed string parameters where a misplaced argument compiles fine and silently sends the
/// wrong field.
/// </summary>
public class NodeServiceDetectionOrientationTests
{
    private static async Task<(NodeService Service, SettingsResolver Settings, Guid CameraId, Guid NodeId)> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);

        // MediaSigningKey must be non-null: GetConfigAsync lazily backfills a missing one with
        // ExecuteUpdateAsync, which the in-memory provider doesn't support.
        var node = new LarisVMS.Core.Entities.Node
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash", MediaSigningKey = "key"
        };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "front-door", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id,
            AiDetectionEnabled = true,
        };
        // The shape that motivated the setting: ONVIF advertises a landscape Sub profile for a
        // corridor-mounted camera that actually sends 480x704.
        var sub = new CameraStream
        {
            Id = Guid.NewGuid(), CameraId = camera.Id, Role = CameraStreamRole.Sub,
            RtspUri = "rtsp://10.0.0.1/sub", Width = 704, Height = 480, Fps = 10, IsEnabled = true,
        };
        db.AddRange(node, camera, sub);
        await db.SaveChangesAsync();

        var settings = new SettingsResolver(db);
        return (new NodeService(db, settings), settings, camera.Id, node.Id);
    }

    [Fact]
    public async Task DefaultsToAutoWhenNothingIsConfigured()
    {
        var (service, _, _, nodeId) = await SeedAsync();

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal("Auto", Assert.Single(config.Cameras).AiDetectionOrientation);
    }

    [Fact]
    public async Task ACameraScopedOverrideReachesThatCamerasConfig()
    {
        var (service, settings, cameraId, nodeId) = await SeedAsync();
        await settings.SetOverrideAsync(SettingScope.Camera, cameraId, "AiDetection.Orientation", "Portrait");

        var config = await service.GetConfigAsync(nodeId);

        var camera = Assert.Single(config.Cameras);
        Assert.Equal("Portrait", camera.AiDetectionOrientation);
        // The neighbouring positional string parameter must not have been displaced by the new one.
        Assert.Equal("Sub", camera.AiDetectionStreamRole);
    }

    [Fact]
    public async Task ACameraOverrideBeatsTheGlobalDefault()
    {
        var (service, settings, cameraId, nodeId) = await SeedAsync();
        await settings.SetGlobalAsync("AiDetection.Orientation", "Landscape");
        await settings.SetOverrideAsync(SettingScope.Camera, cameraId, "AiDetection.Orientation", "Portrait");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal("Portrait", Assert.Single(config.Cameras).AiDetectionOrientation);
    }
}
