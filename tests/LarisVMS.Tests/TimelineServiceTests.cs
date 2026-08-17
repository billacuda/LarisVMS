using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
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

    /// <summary>An admin who has customized nothing, which is what these tests assert against —
    /// EventPalette.Empty resolves every color to its built-in default.</summary>
    private static readonly IEventColorService DefaultPalette = new StubEventColors(EventPalette.Empty);

    private sealed class StubEventColors(EventPalette palette) : IEventColorService
    {
        public Task<EventPalette> GetAsync(CancellationToken ct = default) => Task.FromResult(palette);
        public Task SaveAsync(EventPalette p, string? modifiedBy, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ADetectionSpanUsesTheAdminConfiguredColorForItsClass()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId,
            StartUtc = start,
            EndUtc = start.AddSeconds(30),
            Source = MotionSource.CameraEvent,
            DetectionKind = DetectionKind.Person
        });
        await db.SaveChangesAsync();

        var custom = new StubEventColors(new EventPalette(null, null,
            new Dictionary<DetectionKind, string> { [DetectionKind.Person] = "#123456" }));
        var service = new TimelineService(db, custom);

        var buckets = await service.GetBucketsAsync(cameraId, start, start.AddMinutes(1), 2);

        Assert.Equal("#123456", buckets[0].TagColorHex);
    }

    [Fact]
    public async Task ADetectionSpanFallsBackToItsBuiltInColorWhenNothingIsConfigured()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId,
            StartUtc = start,
            EndUtc = start.AddSeconds(30),
            Source = MotionSource.CameraEvent,
            DetectionKind = DetectionKind.Vehicle
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);

        var buckets = await service.GetBucketsAsync(cameraId, start, start.AddMinutes(1), 2);

        Assert.Equal(DetectionDisplay.ColorHex(DetectionKind.Vehicle), buckets[0].TagColorHex);
    }

    [Fact]
    public async Task ABucketOverlappingSeveralClassesCarriesEveryColorNotJustTheWinner()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        foreach (var kind in new[] { DetectionKind.Person, DetectionKind.Vehicle })
        {
            db.MotionSpans.Add(new MotionSpan
            {
                CameraId = cameraId,
                StartUtc = start,
                EndUtc = start.AddSeconds(30),
                Source = MotionSource.CameraEvent,
                DetectionKind = kind
            });
        }
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var buckets = await service.GetBucketsAsync(cameraId, start, start.AddMinutes(1), 2);

        Assert.Equal(2, buckets[0].TagColorHexes!.Count);
        Assert.Contains(DetectionDisplay.ColorHex(DetectionKind.Person), buckets[0].TagColorHexes!);
        Assert.Contains(DetectionDisplay.ColorHex(DetectionKind.Vehicle), buckets[0].TagColorHexes!);
        // The single-color field stays populated so anything reading one color still renders.
        Assert.Equal(buckets[0].TagColorHexes![0], buckets[0].TagColorHex);
    }

    [Fact]
    public async Task ABucketWithNoTaggedSpansHasNoColorList()
    {
        // Every bucket on a camera with no object analytics and no tag rules — the list must stay
        // null there rather than becoming an empty array on every bucket of every timeline.
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = from, EndUtc = from.AddMinutes(1), FilePath = "a.mp4"
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var buckets = await service.GetBucketsAsync(cameraId, from, from.AddMinutes(2), 2);

        Assert.All(buckets, b => Assert.Null(b.TagColorHexes));
        Assert.All(buckets, b => Assert.Null(b.TagColorHex));
    }

    [Fact]
    public async Task RepeatedSpansOfOneClassCollapseToASingleColorBand()
    {
        // The 15s checkpoint loop writes many rows for one open detection; banding by row rather
        // than by distinct color would shred the bar into identical stripes.
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            db.MotionSpans.Add(new MotionSpan
            {
                CameraId = cameraId,
                StartUtc = start.AddSeconds(i),
                EndUtc = start.AddSeconds(20 + i),
                Source = MotionSource.CameraEvent,
                DetectionKind = DetectionKind.Person
            });
        }
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var buckets = await service.GetBucketsAsync(cameraId, start, start.AddMinutes(1), 2);

        Assert.Single(buckets[0].TagColorHexes!);
    }

    [Fact]
    public async Task ACameraSeeingSeveralObjectClassesAtOnceReportsABadgeForEach()
    {
        // A person walking a dog past a parked car is three classes on one stream, and the tile has
        // to say so — collapsing to whichever the query happened to return first would silently drop
        // the other two.
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        foreach (var kind in new[] { DetectionKind.Person, DetectionKind.Vehicle, DetectionKind.Animal })
        {
            db.MotionSpans.Add(new MotionSpan
            {
                CameraId = cameraId,
                StartUtc = now.AddSeconds(-20),
                EndUtc = now,
                Source = MotionSource.CameraEvent,
                DetectionKind = kind
            });
        }
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var states = await service.GetActiveDetectionsAsync();

        var state = Assert.Single(states);
        Assert.Equal(3, state.Detections.Count);
        Assert.Equal(
            new[] { "Person", "Vehicle", "Animal" }.OrderBy(s => s),
            state.Detections.Select(d => d.Kind).OrderBy(s => s));
        // Each badge carries its own display fields, so two classes can never render identically.
        Assert.Equal(3, state.Detections.Select(d => d.Emoji).Distinct().Count());
        Assert.Equal(3, state.Detections.Select(d => d.ColorHex).Distinct().Count());
    }

    [Fact]
    public async Task RepeatedSpansOfTheSameClassProduceOneBadgeNotSeveral()
    {
        // The checkpoint loop writes a row roughly every 15s while a detection is open, so the same
        // class legitimately appears many times in the window.
        using var db = NewDb();
        var cameraId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
        {
            db.MotionSpans.Add(new MotionSpan
            {
                CameraId = cameraId,
                StartUtc = now.AddSeconds(-30 + i),
                EndUtc = now.AddSeconds(-10 + i),
                Source = MotionSource.CameraEvent,
                DetectionKind = DetectionKind.Person
            });
        }
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var states = await service.GetActiveDetectionsAsync();

        Assert.Single(Assert.Single(states).Detections);
    }

    [Fact]
    public async Task DetectionsAreReportedPerCameraNotPooledAcrossThem()
    {
        using var db = NewDb();
        var cameraA = Guid.NewGuid();
        var cameraB = Guid.NewGuid();
        var now = DateTime.UtcNow;
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraA, StartUtc = now.AddSeconds(-20), EndUtc = now,
            Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Person
        });
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraB, StartUtc = now.AddSeconds(-20), EndUtc = now,
            Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Vehicle
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var states = await service.GetActiveDetectionsAsync();

        Assert.Equal(2, states.Count);
        Assert.Equal("Person", Assert.Single(states.Single(s => s.CameraId == cameraA).Detections).Kind);
        Assert.Equal("Vehicle", Assert.Single(states.Single(s => s.CameraId == cameraB).Detections).Kind);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
        var segments = await service.GetSegmentsAsync(cameraId, baseTime, baseTime.AddHours(1));

        Assert.Equal(2, segments.Count);
        Assert.Equal(baseTime.AddMinutes(10), segments[0].StartUtc);
        Assert.Equal(baseTime.AddMinutes(30), segments[1].StartUtc);
    }

    [Fact]
    public async Task GetSegmentFilePathsCarriesEachSegmentsOwningNodeSoExportCanDetectAReassignmentSplit()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var otherNodeId = Guid.NewGuid();
        var baseTime = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);

        db.Segments.AddRange(
            new Segment
            {
                CameraId = cameraId, NodeId = otherNodeId, StreamRole = CameraStreamRole.Main,
                StartUtc = baseTime, EndUtc = baseTime.AddMinutes(1), FilePath = "old-node.mp4"
            },
            new Segment
            {
                CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
                StartUtc = baseTime.AddMinutes(5), EndUtc = baseTime.AddMinutes(6), FilePath = "current-node.mp4"
            });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var segments = await service.GetSegmentFilePathsAsync(cameraId, baseTime, baseTime.AddHours(1));

        Assert.Equal(2, segments.Count);
        Assert.Equal("old-node.mp4", segments[0].FilePath);
        Assert.Equal(otherNodeId, segments[0].NodeId);
        Assert.Equal("current-node.mp4", segments[1].FilePath);
        Assert.Equal(nodeId, segments[1].NodeId);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
        var buckets = await service.GetGlobalBucketsAsync(from, from.AddHours(1), 60, []);

        Assert.True(buckets[10].HasRecording);
    }

    [Fact]
    public async Task GlobalBucketsAreEmptyWithNoSegmentsAtAll()
    {
        var db = NewDb();
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
        var active = await service.GetCamerasWithActiveMotionAsync();

        Assert.DoesNotContain(cameraId, active);
    }

    [Fact]
    public async Task CameraWithNoMotionSpansAtAllIsNotActive()
    {
        var (db, cameraId, _) = await SeedCameraAsync();

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
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

        var service = new TimelineService(db, DefaultPalette);
        var buckets = await service.GetBucketsAsync(cameraId, from, from.AddHours(1), 60);

        Assert.Null(buckets[20].TagColorHex);
    }
}
