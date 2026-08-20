using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Covers PtzService's "can this camera even be sent a PTZ command" resolution — every case here
/// short-circuits before any network call, so these are safe, fast unit tests. The actual "resolves
/// and calls a real ONVIF PTZ service" path needs a reachable device and is out of scope here, same
/// as this app's other ONVIF integrations.
/// </summary>
public class PtzServiceTests
{
    private static (ApplicationDbContext Db, PtzService Service) NewService()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        // Never actually invoked by any test here — every case below returns false before PtzService
        // gets far enough to call this.
        return (db, new PtzService(db, () => throw new InvalidOperationException("HTTP should not be reached in this test.")));
    }

    private static Camera NewCamera(Guid id) => new()
    {
        Id = id, Name = "Cam", Host = "192.168.1.50", DeviceServiceUri = "http://192.168.1.50/onvif/device_service",
        IsEnabled = true, CreatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task UnknownCameraReturnsFalse()
    {
        var (_, service) = NewService();
        Assert.False(await service.MoveAsync(Guid.NewGuid(), 1, 0, 0));
        Assert.False(await service.StopAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CameraWithNoCapabilitiesRowReturnsFalse()
    {
        var (db, service) = NewService();
        var camera = NewCamera(Guid.NewGuid());
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();

        Assert.False(await service.MoveAsync(camera.Id, 1, 0, 0));
    }

    [Fact]
    public async Task CameraWithHasPtzFalseReturnsFalse()
    {
        var (db, service) = NewService();
        var camera = NewCamera(Guid.NewGuid());
        db.Cameras.Add(camera);
        db.CameraCapabilities.Add(new CameraCapabilities
        {
            CameraId = camera.Id, HasPtz = false,
            RawProbeJson = """{"PTZ":"http://192.168.1.50/onvif/ptz_service"}"""
        });
        await db.SaveChangesAsync();

        Assert.False(await service.MoveAsync(camera.Id, 1, 0, 0));
    }

    [Fact]
    public async Task CameraWithNoPtzKeyInRawProbeJsonReturnsFalse()
    {
        var (db, service) = NewService();
        var camera = NewCamera(Guid.NewGuid());
        db.Cameras.Add(camera);
        db.CameraCapabilities.Add(new CameraCapabilities
        {
            CameraId = camera.Id, HasPtz = true,
            RawProbeJson = """{"Media":"http://192.168.1.50/onvif/media_service"}"""
        });
        db.CameraStreams.Add(new CameraStream
        {
            Id = Guid.NewGuid(), CameraId = camera.Id, Role = CameraStreamRole.Main,
            ProfileToken = "profile_1", RtspUri = "rtsp://192.168.1.50/main"
        });
        await db.SaveChangesAsync();

        Assert.False(await service.MoveAsync(camera.Id, 1, 0, 0));
    }

    [Fact]
    public async Task CameraWithNoMainStreamProfileTokenReturnsFalse()
    {
        var (db, service) = NewService();
        var camera = NewCamera(Guid.NewGuid());
        db.Cameras.Add(camera);
        db.CameraCapabilities.Add(new CameraCapabilities
        {
            CameraId = camera.Id, HasPtz = true,
            RawProbeJson = """{"PTZ":"http://192.168.1.50/onvif/ptz_service"}"""
        });
        await db.SaveChangesAsync();

        Assert.False(await service.MoveAsync(camera.Id, 1, 0, 0));
    }

    [Fact]
    public async Task CorruptRawProbeJsonReturnsFalseRatherThanThrowing()
    {
        var (db, service) = NewService();
        var camera = NewCamera(Guid.NewGuid());
        db.Cameras.Add(camera);
        db.CameraCapabilities.Add(new CameraCapabilities
        {
            CameraId = camera.Id, HasPtz = true, RawProbeJson = "{not valid json"
        });
        db.CameraStreams.Add(new CameraStream
        {
            Id = Guid.NewGuid(), CameraId = camera.Id, Role = CameraStreamRole.Main,
            ProfileToken = "profile_1", RtspUri = "rtsp://192.168.1.50/main"
        });
        await db.SaveChangesAsync();

        Assert.False(await service.MoveAsync(camera.Id, 1, 0, 0));
    }
}
