using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Covers the wiring of Detection.MaxFps from a Setting row through NodeService.GetConfigAsync onto
/// NodeConfigCameraDto.MaxFps — previously node-scoped only (NodeConfigResponse.MaxDetectionFps,
/// shared by every camera on the node), now resolved per camera (Camera → Node → Global, same chain
/// AiConfidence/MotionJitterPixels already resolve through) so a camera that doesn't need the node's
/// full detection frame rate can be capped independently. The Camera → Node → Global resolution
/// itself is SettingsResolver's own concern (see SettingsResolverTests); what's worth pinning here,
/// same reasoning as NodeServiceDetectionOrientationTests, is that the resolved value actually
/// reaches the right field — NodeConfigCameraDto is a positional record with a long tail of
/// same-typed int parameters where a misplaced argument compiles fine and silently sends the wrong
/// field to the wrong camera property.
/// </summary>
public class NodeServiceMaxFpsTests
{
    private static async Task<(NodeService Service, SettingsResolver Settings, Guid CameraId, Guid NodeId)> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);

        var node = new LarisVMS.Core.Entities.Node
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash", MediaSigningKey = "key",
            StorageRootPath = @"E:\LarisVMS\recordings",
        };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "driveway", Host = "10.0.0.2",
            DeviceServiceUri = "http://10.0.0.2/onvif/device_service", NodeId = node.Id,
            AiDetectionEnabled = true,
        };
        var sub = new CameraStream
        {
            Id = Guid.NewGuid(), CameraId = camera.Id, Role = CameraStreamRole.Sub,
            RtspUri = "rtsp://10.0.0.2/sub", Width = 1280, Height = 720, Fps = 15, IsEnabled = true,
        };
        db.AddRange(node, camera, sub);
        await db.SaveChangesAsync();

        var settings = new SettingsResolver(db);
        return (new NodeService(db, settings), settings, camera.Id, node.Id);
    }

    [Fact]
    public async Task DefaultsToTenWhenNothingIsConfigured()
    {
        var (service, _, _, nodeId) = await SeedAsync();

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal(10, Assert.Single(config.Cameras).MaxFps);
        // The node-scoped field this replaced as the per-camera source of truth must still carry the
        // same resolved value, since NodeWorker's decodeFpsCap now reads camera.MaxFps but an older
        // node build still reads config.MaxDetectionFps.
        Assert.Equal(10, config.MaxDetectionFps);
    }

    [Fact]
    public async Task ACameraScopedOverrideReachesThatCamerasConfigWithoutDisturbingItsNeighbours()
    {
        var (service, settings, cameraId, nodeId) = await SeedAsync();
        await settings.SetOverrideAsync(SettingScope.Camera, cameraId, "Detection.MaxFps", "3");

        var config = await service.GetConfigAsync(nodeId);

        var camera = Assert.Single(config.Cameras);
        Assert.Equal(3, camera.MaxFps);
        // The neighbouring positional parameters (RejectMotionJitter, MotionJitterPixels) must not
        // have been displaced by the newly-appended one.
        Assert.False(camera.RejectMotionJitter);
        Assert.Equal(3, camera.MotionJitterPixels);
        // The node-scoped field is unaffected by a camera-only override — a node with several
        // cameras, only one of which is capped, must not have the cap leak onto its siblings via the
        // shared node-level field.
        Assert.Equal(10, config.MaxDetectionFps);
    }

    [Fact]
    public async Task ACameraOverrideBeatsTheNodeAndGlobalDefault()
    {
        var (service, settings, cameraId, nodeId) = await SeedAsync();
        await settings.SetGlobalAsync("Detection.MaxFps", "20");
        await settings.SetOverrideAsync(SettingScope.Node, nodeId, "Detection.MaxFps", "15");
        await settings.SetOverrideAsync(SettingScope.Camera, cameraId, "Detection.MaxFps", "5");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal(5, Assert.Single(config.Cameras).MaxFps);
        // No camera-level override on the node-scoped resolution — the node default still applies.
        Assert.Equal(15, config.MaxDetectionFps);
    }

    [Fact]
    public async Task ANodeOverrideAppliesToACameraWithNoOwnOverride()
    {
        var (service, settings, _, nodeId) = await SeedAsync();
        await settings.SetOverrideAsync(SettingScope.Node, nodeId, "Detection.MaxFps", "6");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal(6, Assert.Single(config.Cameras).MaxFps);
        Assert.Equal(6, config.MaxDetectionFps);
    }

    [Fact]
    public async Task ZeroMeansNoCapAndIsNotTreatedAsUnset()
    {
        var (service, settings, cameraId, nodeId) = await SeedAsync();
        await settings.SetOverrideAsync(SettingScope.Camera, cameraId, "Detection.MaxFps", "0");

        var config = await service.GetConfigAsync(nodeId);

        Assert.Equal(0, Assert.Single(config.Cameras).MaxFps);
    }
}
