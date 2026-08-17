using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Covers M8 pass 6's two NodeService entry points for ONVIF PullPoint reporting:
/// RecordCameraEventsAsync (the raw log) and RecordMotionSpansAsync's null-ZoneId path (a
/// camera-pushed span, as opposed to a ServerMotion zone's). Same in-memory-DB harness as
/// NodeServiceMotionSpanTests.</summary>
public class NodeServiceCameraEventTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<(ApplicationDbContext Db, NodeService Service, Guid CameraId, Guid NodeId)> SeedAsync()
    {
        var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash", MediaSigningKey = "key" };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id
        };
        db.AddRange(node, camera);
        await db.SaveChangesAsync();
        return (db, new NodeService(db, new SettingsResolver(db)), camera.Id, node.Id);
    }

    [Fact]
    public async Task RecordCameraEventsAsyncInsertsOneRowPerNotification()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var t = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordCameraEventsAsync(nodeId,
        [
            new CameraEventReportItem(cameraId, "tns1:RuleEngine/CellMotionDetector/Motion", t, "{\"State\":\"true\"}", IsMotion: true),
            new CameraEventReportItem(cameraId, "tns1:RuleEngine/CellMotionDetector/Motion", t.AddSeconds(5), "{\"State\":\"false\"}", IsMotion: false),
        ]);

        var rows = await db.CameraEvents.Where(e => e.CameraId == cameraId).OrderBy(e => e.ReceivedUtc).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(t, rows[0].ReceivedUtc);
        Assert.Equal("tns1:RuleEngine/CellMotionDetector/Motion", rows[0].OnvifTopic);
        Assert.Equal("{\"State\":\"true\"}", rows[0].PayloadJson);
    }

    [Fact]
    public async Task RecordCameraEventsAsyncWithNoEventsIsANoOp()
    {
        var (db, service, _, nodeId) = await SeedAsync();
        await service.RecordCameraEventsAsync(nodeId, []);
        Assert.Empty(await db.CameraEvents.ToListAsync());
    }

    [Fact]
    public async Task MotionSpanWithNullZoneIdIsStoredAsCameraEventSource()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(10), 1.0)]);

        var row = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);
        Assert.Null(row.ZoneId);
        Assert.Equal(MotionSource.CameraEvent, row.Source);
    }

    [Fact]
    public async Task RepeatedCameraEventSpanCheckpointsWithNullZoneIdExtendOneRow()
    {
        // Same upsert-by-(CameraId, ZoneId, StartUtc) identity as a ServerMotion span, just with
        // ZoneId being null on both sides of the comparison instead of a real zone id.
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 1.0)]);
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(10), 1.0)]);

        var rows = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(start.AddSeconds(10), rows[0].EndUtc);
    }

    [Fact]
    public async Task ADetectionSpanDoesNotCollideWithPlainMotionStartingAtTheSameInstant()
    {
        // The normal case for an object-capable camera: it fires both a motion topic and a
        // PeopleDetector topic for one real event, and both report with ZoneId and EventTagRuleId
        // null. Without DetectionKind in the upsert identity the second would be treated as a
        // checkpoint of the first and silently vanish.
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 1.0),
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 1.0, null, DetectionKind.Person)
        ]);

        var rows = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.DetectionKind is null);
        Assert.Single(rows, r => r.DetectionKind == DetectionKind.Person);
        // A detection still arrives over the camera-event channel — the class is the extra axis,
        // not a replacement for Source.
        Assert.All(rows, r => Assert.Equal(MotionSource.CameraEvent, r.Source));
    }

    [Fact]
    public async Task TwoDifferentDetectedClassesAtTheSameInstantStaySeparateSpans()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 1.0, null, DetectionKind.Person),
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 1.0, null, DetectionKind.Vehicle)
        ]);

        var rows = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task RepeatedDetectionCheckpointsExtendTheSameRow()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId,
            [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 1.0, null, DetectionKind.Person)]);
        await service.RecordMotionSpansAsync(nodeId,
            [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(30), 1.0, null, DetectionKind.Person)]);

        var row = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);
        Assert.Equal(start.AddSeconds(30), row.EndUtc);
        Assert.Equal(DetectionKind.Person, row.DetectionKind);
    }
}
