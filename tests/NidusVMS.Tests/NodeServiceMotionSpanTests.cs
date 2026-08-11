using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Entities;
using NidusVMS.Infrastructure.Data;
using NidusVMS.Infrastructure.Services;

namespace NidusVMS.Tests;

/// <summary>Covers NodeService.RecordMotionSpansAsync's upsert-by-(CameraId, ZoneId, StartUtc)
/// behavior — the fix for a long-running span being invisible in MotionSpans until it eventually
/// closes. Without this, every periodic checkpoint of the same still-open span (NodeWorker
/// .EnqueueMotionCheckpoints) would have piled up as a new row instead of extending one.</summary>
public class NodeServiceMotionSpanTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<(ApplicationDbContext Db, NodeService Service, Guid CameraId, Guid NodeId, Guid ZoneId)> SeedAsync()
    {
        var db = NewDb();
        var node = new NidusVMS.Core.Entities.Node
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash", MediaSigningKey = "key"
        };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id
        };
        var zone = new Zone
        {
            Id = Guid.NewGuid(), CameraId = camera.Id, Name = "front-yard",
            Kind = NidusVMS.Core.Enums.ZoneKind.ServerMotion, PolygonJson = "{}"
        };
        db.AddRange(node, camera, zone);
        await db.SaveChangesAsync();
        return (db, new NodeService(db, new SettingsResolver(db)), camera.Id, node.Id, zone.Id);
    }

    [Fact]
    public async Task RepeatedCheckpointsForTheSameOpenSpanExtendOneRowInsteadOfCreatingMany()
    {
        var (db, service, cameraId, nodeId, zoneId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        // Three checkpoints of the same still-open span, same StartUtc each time (as
        // MotionHysteresis.CurrentInProgressSpan always reports it), EndUtc creeping forward.
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, zoneId, start, start.AddSeconds(15), 0.4)]);
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, zoneId, start, start.AddSeconds(30), 0.7)]);
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, zoneId, start, start.AddSeconds(45), 0.5)]);

        var rows = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();

        Assert.Single(rows);
        Assert.Equal(start, rows[0].StartUtc);
        Assert.Equal(start.AddSeconds(45), rows[0].EndUtc); // extended to the latest checkpoint
        Assert.Equal(0.7, rows[0].Score); // peak across all checkpoints, not just the last one
    }

    [Fact]
    public async Task ADifferentStartUtcOnTheSameCameraAndZoneCreatesASeparateRow()
    {
        var (db, service, cameraId, nodeId, zoneId) = await SeedAsync();
        var firstStart = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        var secondStart = firstStart.AddHours(1); // a genuinely separate, later motion event

        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, zoneId, firstStart, firstStart.AddSeconds(10), 0.5)]);
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, zoneId, secondStart, secondStart.AddSeconds(10), 0.5)]);

        var rows = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task EndUtcNeverMovesBackwardOnAnOutOfOrderReport()
    {
        var (db, service, cameraId, nodeId, zoneId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, zoneId, start, start.AddSeconds(30), 0.5)]);
        // A stale/out-of-order retry reporting an earlier EndUtc than what's already stored.
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, zoneId, start, start.AddSeconds(15), 0.5)]);

        var row = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);

        Assert.Equal(start.AddSeconds(30), row.EndUtc);
    }
}
