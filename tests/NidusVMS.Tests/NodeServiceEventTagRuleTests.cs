using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Enums;
using NidusVMS.Infrastructure.Data;
using NidusVMS.Infrastructure.Services;

namespace NidusVMS.Tests;

/// <summary>Covers NodeService.RecordMotionSpansAsync's M8 pass 8 extensions: three-way Source
/// inference (CustomTag/CameraEvent/ServerMotion) and EventTagRuleId joining ZoneId in the
/// upsert-identity key — without the latter, a custom-tag span and the built-in camera-pushed
/// classifier's own span (both ZoneId=null) starting at the exact same instant would collide and
/// silently steal each other's checkpoints.</summary>
public class NodeServiceEventTagRuleTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<(ApplicationDbContext Db, NodeService Service, Guid CameraId, Guid NodeId, Guid ZoneId, Guid RuleId)> SeedAsync()
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
            Kind = ZoneKind.ServerMotion, PolygonJson = "{}"
        };
        var rule = new EventTagRule
        {
            Id = Guid.NewGuid(), CameraId = camera.Id, Name = "Person detected",
            StartTopic = "tns1:Custom/Start", StopTopic = "tns1:Custom/Stop",
            ColorHex = "#ff9800", DrivesRecording = true, IsEnabled = true
        };
        db.AddRange(node, camera, zone, rule);
        await db.SaveChangesAsync();
        return (db, new NodeService(db, new SettingsResolver(db)), camera.Id, node.Id, zone.Id, rule.Id);
    }

    [Fact]
    public async Task ServerMotionSpanWithNonNullZoneIdGetsServerMotionSource()
    {
        var (db, service, cameraId, nodeId, zoneId, _) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, zoneId, start, start.AddSeconds(10), 0.5)]);

        var row = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);
        Assert.Equal(MotionSource.ServerMotion, row.Source);
    }

    [Fact]
    public async Task BuiltInCameraPushedSpanWithNullZoneIdAndNullRuleIdGetsCameraEventSource()
    {
        var (db, service, cameraId, nodeId, _, _) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(10), 0.5)]);

        var row = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);
        Assert.Equal(MotionSource.CameraEvent, row.Source);
        Assert.Null(row.EventTagRuleId);
    }

    [Fact]
    public async Task CustomTagSpanWithNonNullEventTagRuleIdGetsCustomTagSourceEvenWithNullZoneId()
    {
        var (db, service, cameraId, nodeId, _, ruleId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(10), 0.5, ruleId)]);

        var row = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);
        Assert.Equal(MotionSource.CustomTag, row.Source);
        Assert.Equal(ruleId, row.EventTagRuleId);
    }

    [Fact]
    public async Task ACustomTagSpanAndABuiltInSpanStartingAtTheSameInstantDoNotCollide()
    {
        var (db, service, cameraId, nodeId, _, ruleId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        // Both report ZoneId=null and the exact same StartUtc — before EventTagRuleId joined the
        // upsert-identity key, the second call here would have matched and silently extended the
        // first call's row instead of creating its own.
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.5, null)]);
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.9, ruleId)]);

        var rows = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.EventTagRuleId is null && r.Source == MotionSource.CameraEvent);
        Assert.Contains(rows, r => r.EventTagRuleId == ruleId && r.Source == MotionSource.CustomTag);
    }

    [Fact]
    public async Task RepeatedCheckpointsForTheSameOpenCustomTagSpanExtendOneRow()
    {
        var (db, service, cameraId, nodeId, _, ruleId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(15), 0.4, ruleId)]);
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(30), 0.7, ruleId)]);

        var rows = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(start.AddSeconds(30), rows[0].EndUtc);
        Assert.Equal(0.7, rows[0].Score);
    }
}
