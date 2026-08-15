using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

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
        var node = new LarisVMS.Core.Entities.Node
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
    public async Task BucketsCoveringAMotionSpanAreFlaggedMotionIndependentlyOfRecording()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(1);
        // No Segment at all — HasMotion must not be coupled to HasRecording.
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.ServerMotion,
            StartUtc = from.AddMinutes(20), EndUtc = from.AddMinutes(21), Score = 0.5
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var buckets = await service.GetBucketsAsync(cameraId, from, to, 60);

        Assert.True(buckets[20].HasMotion);
        Assert.False(buckets[20].HasRecording);
        Assert.False(buckets[0].HasMotion);
        Assert.False(buckets[59].HasMotion);
    }

    [Fact]
    public async Task GlobalBucketsAggregateMotionAcrossCameras()
    {
        var (db, cameraId1, _) = await SeedCameraAsync();
        var camera2 = new Camera
        {
            Id = Guid.NewGuid(), Name = "cam-2", Host = "10.0.0.2",
            DeviceServiceUri = "http://10.0.0.2/onvif/device_service"
        };
        db.Cameras.Add(camera2);
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = camera2.Id, Source = MotionSource.ServerMotion,
            StartUtc = from.AddMinutes(5), EndUtc = from.AddMinutes(6), Score = 0.9
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var buckets = await service.GetGlobalBucketsAsync(from, from.AddHours(1), 60);

        // Motion on cam-2 shows up in the merged timeline even though the query wasn't scoped to
        // cam-1 (or any specific camera) — this is the "did anything happen anywhere" view.
        Assert.True(buckets[5].HasMotion);
        Assert.False(buckets[0].HasMotion);
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
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash" };
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
    public async Task GlobalBucketsScopedToCameraIdsIgnoreActivityFromCamerasOutsideTheSet()
    {
        // The fix for "the overall timeline should only show events/recording for the cameras in
        // the selected view" — cam-2's segment must not show up when the caller only asked about
        // cam-1, even though the unscoped (cameraIds: null) behavior would have included it.
        var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash" };
        var camera1 = new Camera { Id = Guid.NewGuid(), Name = "cam-1", Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id };
        var camera2 = new Camera { Id = Guid.NewGuid(), Name = "cam-2", Host = "10.0.0.2", DeviceServiceUri = "http://10.0.0.2/onvif/device_service", NodeId = node.Id };
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        db.AddRange(node, camera1, camera2);
        db.Segments.AddRange(
            new Segment { CameraId = camera1.Id, NodeId = node.Id, StreamRole = CameraStreamRole.Main, StartUtc = from.AddMinutes(10), EndUtc = from.AddMinutes(11), FilePath = "a.mp4" },
            new Segment { CameraId = camera2.Id, NodeId = node.Id, StreamRole = CameraStreamRole.Main, StartUtc = from.AddMinutes(40), EndUtc = from.AddMinutes(41), FilePath = "b.mp4" });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var scoped = await service.GetGlobalBucketsAsync(from, from.AddHours(1), 60, [camera1.Id]);
        var unscoped = await service.GetGlobalBucketsAsync(from, from.AddHours(1), 60);

        Assert.True(scoped[10].HasRecording);   // cam-1's own segment
        Assert.False(scoped[40].HasRecording);  // cam-2's segment excluded — outside the requested set
        Assert.True(unscoped[40].HasRecording); // sanity check: cam-2's segment IS there without scoping
    }

    [Fact]
    public async Task GlobalBucketsWithAnEmptyCameraIdsListFallsBackToEveryCamera()
    {
        // Same fallback as null — a caller with a genuinely empty scope (e.g. a view with zero
        // cameras) gets the original "merged across everything" behavior rather than an empty
        // result, matching GetGlobalBucketsAsync's own doc comment.
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = from.AddMinutes(10), EndUtc = from.AddMinutes(11), FilePath = "a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var buckets = await service.GetGlobalBucketsAsync(from, from.AddHours(1), 60, []);

        Assert.True(buckets[10].HasRecording);
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

    // ── GetThumbnailInfoAsync (M7 pass 2 — hover thumbnails) ────────────────

    [Fact]
    public async Task GetThumbnailInfoReturnsTheSegmentCoveringTheBucketedInstant()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        // 5-minute-aligned segment start — the common case (segment_atclocktime=1 in production).
        var segmentStart = new DateTime(2026, 8, 9, 0, 5, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = segmentStart, EndUtc = segmentStart.AddSeconds(60), DurationMs = 60000,
            FilePath = @"C:\rec\a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        // Any raw instant inside the [00:05:00, 00:10:00) bucket must resolve here.
        var info = await service.GetThumbnailInfoAsync(cameraId, segmentStart.AddSeconds(42));

        Assert.NotNull(info);
        Assert.Equal(@"C:\rec\a.mp4", info!.FilePath);
        Assert.Equal(0, info.OffsetSeconds); // bucket floor lands exactly on the segment's own start
        Assert.Equal("10.0.0.5", info.NodeIp);
        Assert.Equal(8554, info.NodeLivePort);
        Assert.Equal("key", info.NodeMediaSigningKey);
    }

    [Fact]
    public async Task GetThumbnailInfoReturnsNullWhenNoSegmentCoversTheBucketedInstant()
    {
        var (db, cameraId, _) = await SeedCameraAsync();

        var service = new TimelineService(db);
        var info = await service.GetThumbnailInfoAsync(cameraId, new DateTime(2026, 8, 9, 0, 5, 0, DateTimeKind.Utc));

        Assert.Null(info);
    }

    [Fact]
    public async Task GetThumbnailInfoBucketsDifferentInstantsInTheSameFiveMinuteWindowIdentically()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var segmentStart = new DateTime(2026, 8, 9, 0, 5, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = segmentStart, EndUtc = segmentStart.AddSeconds(60), DurationMs = 60000,
            FilePath = "a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        // 00:05:00 and 00:09:59 both floor to the same 00:05:00 bucket, even though only the first
        // few seconds of that bucket actually have a segment.
        var early = await service.GetThumbnailInfoAsync(cameraId, segmentStart);
        var late = await service.GetThumbnailInfoAsync(cameraId, segmentStart.AddMinutes(4).AddSeconds(59));

        Assert.NotNull(early);
        Assert.NotNull(late);
        Assert.Equal(early!.FilePath, late!.FilePath);
        Assert.Equal(early.OffsetSeconds, late.OffsetSeconds);
    }

    [Fact]
    public async Task GetThumbnailInfoClampsTheOffsetToStayInsideTheSegmentsActualDuration()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        // Straddles the 00:05:00 boundary (starts 10s before it) so the bucketed instant falls 10s
        // into the segment, but DurationMs (5s) says there's only 5s of real content — models a
        // segment whose reported duration is shorter than its StartUtc/EndUtc span would suggest.
        var segmentStart = new DateTime(2026, 8, 9, 0, 4, 50, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = segmentStart, EndUtc = segmentStart.AddSeconds(60), DurationMs = 5000,
            FilePath = "a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var info = await service.GetThumbnailInfoAsync(cameraId, segmentStart.AddSeconds(10));

        Assert.NotNull(info);
        Assert.Equal(4, info!.OffsetSeconds); // clamped to DurationMs/1000 - 1, not the raw 10s
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

    [Fact]
    public async Task CamerasWithARecentMotionSpanCheckpointAreActive()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.ServerMotion,
            StartUtc = DateTime.UtcNow.AddMinutes(-5), EndUtc = DateTime.UtcNow.AddSeconds(-2), Score = 0.5
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var active = await service.GetCamerasWithActiveMotionAsync();

        Assert.Contains(cameraId, active);
    }

    [Fact]
    public async Task CamerasWithOnlyAStaleMotionSpanAreNotActive()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.ServerMotion,
            StartUtc = DateTime.UtcNow.AddMinutes(-10), EndUtc = DateTime.UtcNow.AddMinutes(-5), Score = 0.5
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var active = await service.GetCamerasWithActiveMotionAsync();

        Assert.DoesNotContain(cameraId, active);
    }

    [Fact]
    public async Task CameraWithNoMotionSpansAtAllIsNotActive()
    {
        var (db, cameraId, _) = await SeedCameraAsync();

        var service = new TimelineService(db);
        var active = await service.GetCamerasWithActiveMotionAsync();

        Assert.DoesNotContain(cameraId, active);
    }

    // ── EventTagRule color (M8 pass 8) ─────────────────────────────────────

    [Fact]
    public async Task BucketCoveringACustomTagSpanCarriesTheRulesColor()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        var rule = new EventTagRule
        {
            Id = Guid.NewGuid(), CameraId = cameraId, Name = "Package", StartTopic = "tns1:Custom/Start",
            ColorHex = "#ff9800", DrivesRecording = false, IsEnabled = true
        };
        db.EventTagRules.Add(rule);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.CustomTag, EventTagRuleId = rule.Id,
            StartUtc = from.AddMinutes(20), EndUtc = from.AddMinutes(21), Score = 1.0
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var buckets = await service.GetBucketsAsync(cameraId, from, from.AddHours(1), 60);

        Assert.Equal("#ff9800", buckets[20].TagColorHex);
        Assert.True(buckets[20].HasMotion); // still counts as motion for the built-in green/blue logic too
        Assert.Null(buckets[0].TagColorHex);
    }

    [Fact]
    public async Task BucketWithOnlyBuiltInMotionHasNoTagColor()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.ServerMotion,
            StartUtc = from.AddMinutes(20), EndUtc = from.AddMinutes(21), Score = 0.5
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db);
        var buckets = await service.GetBucketsAsync(cameraId, from, from.AddHours(1), 60);

        Assert.Null(buckets[20].TagColorHex);
    }
}
