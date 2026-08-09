using Microsoft.EntityFrameworkCore;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Enums;
using NidusVMS.Infrastructure.Data;
using NidusVMS.Infrastructure.Services;

namespace NidusVMS.Tests;

/// <summary>Covers the M7 timeline bucketing/segment-list/playback-lookup queries — the pieces of
/// M7 that don't need a browser to verify, unlike the canvas timeline and MSE player.</summary>
public class TimelineServiceTests
{
    private static ApplicationDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task<(ApplicationDbContext Db, Guid CameraId, Guid NodeId)> SeedCameraAsync()
    {
        var db = NewDb();
        var node = new NidusVMS.Core.Entities.Node
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash",
            LastIpAddress = "10.0.0.5", LivePort = 8554, MediaSigningKey = "key"
        };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id
        };
        db.AddRange(node, camera);
        await db.SaveChangesAsync();
        return (db, camera.Id, node.Id);
    }

    [Fact]
    public async Task BucketsCoveringASegmentAreFlaggedRecorded()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(1);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = from.AddMinutes(20), EndUtc = from.AddMinutes(21), FilePath = "a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var buckets = await service.GetBucketsAsync(cameraId, from, to, 60);

        Assert.Equal(60, buckets.Count);
        // Segment covers minute 20 — that bucket (and only ones actually overlapping it) is recorded.
        Assert.True(buckets[20].HasRecording);
        Assert.False(buckets[0].HasRecording);
        Assert.False(buckets[59].HasRecording);
    }

    [Fact]
    public async Task BucketsWithNoSegmentsAreAllGaps()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);

        var service = new TimelineService(db);
        var buckets = await service.GetBucketsAsync(cameraId, from, from.AddHours(1), 10);

        Assert.Equal(10, buckets.Count);
        Assert.All(buckets, b => Assert.False(b.HasRecording));
    }

    [Fact]
    public async Task BucketsSpanTheFullRequestedRangeWithNoGapAtTheEnd()
    {
        // bucketTicks is computed via integer division, so the naive fromUtc + bucketTicks*bucketCount
        // can undershoot toUtc by the remainder — the last bucket must absorb it, not leave a sliver
        // of the requested range uncovered.
        var (db, cameraId, _) = await SeedCameraAsync();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddSeconds(100); // 100 / 3 buckets doesn't divide evenly

        var service = new TimelineService(db);
        var buckets = await service.GetBucketsAsync(cameraId, from, to, 3);

        Assert.Equal(from, buckets[0].StartUtc);
        Assert.Equal(to, buckets[^1].EndUtc);
    }

    [Fact]
    public async Task GetSegmentsReturnsOnlySegmentsOverlappingTheRangeInStartOrder()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var baseTime = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);

        Segment Make(int startMin, int endMin, string path) => new()
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = baseTime.AddMinutes(startMin), EndUtc = baseTime.AddMinutes(endMin), FilePath = path
        };

        db.Segments.AddRange(
            Make(30, 31, "in-range-later.mp4"),
            Make(10, 11, "in-range-earlier.mp4"),
            Make(200, 201, "out-of-range.mp4"));
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var segments = await service.GetSegmentsAsync(cameraId, baseTime, baseTime.AddHours(1));

        Assert.Equal(2, segments.Count);
        Assert.Equal(baseTime.AddMinutes(10), segments[0].StartUtc);
        Assert.Equal(baseTime.AddMinutes(30), segments[1].StartUtc);
    }

    [Fact]
    public async Task GetSegmentForPlaybackReturnsTheOwningNodesMediaInfo()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var segment = new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = DateTime.UtcNow.AddMinutes(-1), EndUtc = DateTime.UtcNow, FilePath = @"C:\rec\a.mp4"
        };
        db.Segments.Add(segment);
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var info = await service.GetSegmentForPlaybackAsync(cameraId, segment.Id);

        Assert.NotNull(info);
        Assert.Equal(@"C:\rec\a.mp4", info!.FilePath);
        Assert.Equal("10.0.0.5", info.NodeIp);
        Assert.Equal(8554, info.NodeLivePort);
        Assert.Equal("key", info.NodeMediaSigningKey);
    }

    // ── Query-range UTC normalization ───────────────────────────────────────
    // ASP.NET binds a "...Z" query-string value to Kind=Local (converted to the server's zone),
    // and SQL Server's datetime2 carries no offset — so without normalization every range query
    // silently searched a window shifted by the server's UTC offset. Symptom in practice: the
    // timeline still rendered plausibly (its whole window shifts together) but no segment was ever
    // found covering the instant playback actually asked for, so video never loaded.

    [Fact]
    public void NormalizeToUtcConvertsALocalKindValueToTheSameInstant()
    {
        var utc = new DateTime(2026, 8, 9, 4, 45, 31, DateTimeKind.Utc);
        var asLocal = utc.ToLocalTime(); // what query-string binding actually produces for "...Z"
        Assert.Equal(DateTimeKind.Local, asLocal.Kind);

        var normalized = TimelineService.NormalizeToUtc(asLocal);

        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(utc, normalized);
    }

    [Fact]
    public void NormalizeToUtcLeavesAnAlreadyUtcValueAlone()
    {
        var utc = new DateTime(2026, 8, 9, 4, 45, 31, DateTimeKind.Utc);
        Assert.Equal(utc, TimelineService.NormalizeToUtc(utc));
    }

    [Fact]
    public void NormalizeToUtcTreatsUnspecifiedAsAlreadyUtcRatherThanLocal()
    {
        // A client that omits the trailing Z must not reintroduce the offset shift.
        var unspecified = new DateTime(2026, 8, 9, 4, 45, 31, DateTimeKind.Unspecified);

        var normalized = TimelineService.NormalizeToUtc(unspecified);

        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
        Assert.Equal(unspecified.Ticks, normalized.Ticks); // same wall clock, just stamped UTC
    }

    [Fact]
    public async Task GetSegmentsFindsASegmentWhenTheRangeArrivesAsLocalKind()
    {
        // The end-to-end regression: same instant expressed the way binding delivers it must
        // return the same segment as a true-UTC range would.
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var startUtc = new DateTime(2026, 8, 9, 4, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = startUtc, EndUtc = startUtc.AddMinutes(1), FilePath = "a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var localFrom = startUtc.AddMinutes(-30).ToLocalTime();
        var localTo = startUtc.AddMinutes(30).ToLocalTime();

        var segments = await service.GetSegmentsAsync(cameraId, localFrom, localTo);

        Assert.Single(segments);
        Assert.Equal(startUtc, segments[0].StartUtc);
    }

    [Fact]
    public async Task GlobalBucketsAreRecordedIfAnyCameraHasFootageThere()
    {
        var db = NewDb();
        var node = new NidusVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash" };
        var camera1 = new Camera { Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id };
        var camera2 = new Camera { Id = Guid.NewGuid(), Name = "cam-2", Host = "10.0.0.2", DeviceServiceUri = "http://10.0.0.2/onvif/device_service", NodeId = node.Id };
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        db.AddRange(node, camera1, camera2);
        db.Segments.AddRange(
            new Segment { CameraId = camera1.Id, NodeId = node.Id, StreamRole = CameraStreamRole.Main, StartUtc = from.AddMinutes(10), EndUtc = from.AddMinutes(11), FilePath = "a.mp4" },
            new Segment { CameraId = camera2.Id, NodeId = node.Id, StreamRole = CameraStreamRole.Main, StartUtc = from.AddMinutes(40), EndUtc = from.AddMinutes(41), FilePath = "b.mp4" });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var buckets = await service.GetGlobalBucketsAsync(from, from.AddHours(1), 60);

        // Neither camera alone covers both minute 10 and minute 40 — only the merge does.
        Assert.True(buckets[10].HasRecording);
        Assert.True(buckets[40].HasRecording);
        Assert.False(buckets[25].HasRecording);
    }

    [Fact]
    public async Task GlobalBucketsAreEmptyWithNoSegmentsAtAll()
    {
        var db = NewDb();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);

        var service = new TimelineService(db);
        var buckets = await service.GetGlobalBucketsAsync(from, from.AddHours(1), 10);

        Assert.Equal(10, buckets.Count);
        Assert.All(buckets, b => Assert.False(b.HasRecording));
    }

    [Fact]
    public async Task GetSegmentForPlaybackReturnsNullForASegmentBelongingToAnotherCamera()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var segment = new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = DateTime.UtcNow.AddMinutes(-1), EndUtc = DateTime.UtcNow, FilePath = "a.mp4"
        };
        db.Segments.Add(segment);
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var info = await service.GetSegmentForPlaybackAsync(Guid.NewGuid(), segment.Id);

        Assert.Null(info);
    }
}
