using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Covers the object detection plan decision 5's find-or-create-category +
/// auto-color-assign behavior inside NodeService.RecordMotionSpansAsync, and decision 10's
/// best-frame overwrite-on-checkpoint behavior — the AI-detection-specific extensions to the same
/// upsert path NodeServiceMotionSpanTests already covers for plain motion.</summary>
public class NodeServiceAiDetectionTests
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
    public async Task FirstSightingOfANewCategoryCreatesARowWithAnAutoAssignedColor()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car")
        ]);

        var categories = await db.DetectedObjectCategories.ToListAsync();
        Assert.Single(categories);
        Assert.Equal("Vehicle", categories[0].Name);
        Assert.False(string.IsNullOrWhiteSpace(categories[0].ColorHex));

        var span = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);
        Assert.Equal(MotionSource.AiDetection, span.Source);
        Assert.Equal(categories[0].Id, span.DetectedObjectCategoryId);
        Assert.Equal("car", span.DetectedObjectLabel);
        Assert.Null(span.DetectionKind); // DetectionKind stays untouched by AI-detection spans
    }

    [Fact]
    public async Task SecondSightingOfAKnownCategoryReusesTheExistingRowAndColor()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car")
        ]);
        var firstColor = (await db.DetectedObjectCategories.SingleAsync()).ColorHex;

        // A later, unrelated detection event of the same category.
        var laterStart = start.AddHours(1);
        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, laterStart, laterStart.AddSeconds(5), 0.8,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "truck")
        ]);

        var categories = await db.DetectedObjectCategories.ToListAsync();
        Assert.Single(categories); // no duplicate "Vehicle" row
        Assert.Equal(firstColor, categories[0].ColorHex); // color never re-chosen after creation

        var spans = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();
        Assert.Equal(2, spans.Count); // still two distinct spans — different label, different instant
    }

    [Fact]
    public async Task DifferentCategoriesGetDifferentColors()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car"),
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.7,
                DetectedObjectCategory: "Animal", DetectedObjectLabel: "dog"),
        ]);

        var categories = await db.DetectedObjectCategories.ToListAsync();
        Assert.Equal(2, categories.Count);
        Assert.NotEqual(categories[0].ColorHex, categories[1].ColorHex);
    }

    [Fact]
    public async Task DifferentLabelsAtTheSameInstantProduceSeparateSpansNotOneMergedRow()
    {
        // Two different object classes detected in the same reporting tick, same StartUtc — must
        // not collide into one row the way they would if only DetectedObjectCategoryId (the
        // coarser bucket) were part of the upsert identity instead of the specific label.
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car"),
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.6,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "truck"),
        ]);

        var spans = await db.MotionSpans.Where(m => m.CameraId == cameraId).ToListAsync();

        Assert.Equal(2, spans.Count);
        Assert.Contains(spans, s => s.DetectedObjectLabel == "car");
        Assert.Contains(spans, s => s.DetectedObjectLabel == "truck");
    }

    [Fact]
    public async Task ACheckpointOverwritesTheBestFrameFieldsRatherThanComparing()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        var firstBestAt = start.AddSeconds(1);
        var secondBestAt = start.AddSeconds(10);

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(15), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car",
                BestFrameAtUtc: firstBestAt, BestBoxX: 0.1, BestBoxY: 0.1, BestBoxW: 0.2, BestBoxH: 0.2, BestBoxConfidence: 0.6)
        ]);

        // A later checkpoint of the *same* still-open span (same label, same StartUtc) with a
        // better best-frame — LarisVMS.Vision.Service's own tracking is monotonic, so the report
        // pipeline trusts it and just overwrites rather than re-comparing.
        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(20), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car",
                BestFrameAtUtc: secondBestAt, BestBoxX: 0.3, BestBoxY: 0.3, BestBoxW: 0.5, BestBoxH: 0.5, BestBoxConfidence: 0.95)
        ]);

        var span = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);

        Assert.Equal(secondBestAt, span.BestFrameAtUtc);
        Assert.Equal(0.95, span.BestBoxConfidence);
        Assert.Equal(0.5, span.BestBoxW);
    }

    [Fact]
    public async Task ANonAiDetectionSpanNeverGetsACategory()
    {
        var (db, service, cameraId, nodeId) = await SeedAsync();
        var start = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

        // Plain built-in CameraEvent motion — no DetectedObjectCategory at all.
        await service.RecordMotionSpansAsync(nodeId, [new MotionSpanReportItem(cameraId, null, start, start.AddSeconds(5), 0.5)]);

        var span = await db.MotionSpans.SingleAsync(m => m.CameraId == cameraId);

        Assert.Equal(MotionSource.CameraEvent, span.Source);
        Assert.Null(span.DetectedObjectCategoryId);
        Assert.Null(span.DetectedObjectLabel);
        Assert.Empty(await db.DetectedObjectCategories.ToListAsync());
    }
}
