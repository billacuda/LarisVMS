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

    /// <summary>A deliberately minimal ISettingsResolver stub for GetSnapshotsAsync's type-filter and
    /// pre-roll tests — the real SettingsResolver's cache is a process-wide static
    /// ConcurrentDictionary keyed only by "{cameraId}|{nodeId}|{key}" with no per-DbContext
    /// discriminator, which would leak values across these tests' separate InMemory databases. Only
    /// GetAsync&lt;bool&gt; and GetAsync&lt;int&gt; are exercised by GetSnapshotsAsync; everything
    /// else throws so an accidental new dependency here is loud rather than silently returning a
    /// wrong default.</summary>
    private sealed class StubSettingsResolver(Dictionary<string, bool>? values = null, Dictionary<string, int>? intValues = null) : ISettingsResolver
    {
        public Task<string?> GetRawAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<T> GetAsync<T>(string key, T defaultValue, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
        {
            if (typeof(T) == typeof(bool) && values is not null && values.TryGetValue(key, out var v))
                return Task.FromResult((T)(object)v);
            if (typeof(T) == typeof(int) && intValues is not null && intValues.TryGetValue(key, out var iv))
                return Task.FromResult((T)(object)iv);
            return Task.FromResult(defaultValue);
        }

        public Task<SettingScope?> GetSourceAsync(string key, Guid? cameraId = null, Guid? nodeId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> GetOwnOverrideAsync(SettingScope scope, Guid scopeId, string key, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetGlobalAsync(string key, string value, string? modifiedBy = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SetOverrideAsync(SettingScope scope, Guid scopeId, string key, string? value, string? modifiedBy = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task InvalidateAsync() => Task.CompletedTask;
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
            DetectionKind = DetectionKind.Human
        });
        await db.SaveChangesAsync();

        var custom = new StubEventColors(new EventPalette(null, null,
            new Dictionary<DetectionKind, string> { [DetectionKind.Human] = "#123456" }));
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
        foreach (var kind in new[] { DetectionKind.Human, DetectionKind.Vehicle })
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
        Assert.Contains(DetectionDisplay.ColorHex(DetectionKind.Human), buckets[0].TagColorHexes!);
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
                DetectionKind = DetectionKind.Human
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
        foreach (var kind in new[] { DetectionKind.Human, DetectionKind.Vehicle, DetectionKind.Animal })
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
            new[] { "Human", "Vehicle", "Animal" }.OrderBy(s => s),
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
                DetectionKind = DetectionKind.Human
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
            Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human
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
        Assert.Equal("Human", Assert.Single(states.Single(s => s.CameraId == cameraA).Detections).Kind);
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

    // ── GetSnapshotImageInfoAsync (object detection plan decision 10) ────────

    [Fact]
    public async Task GetSnapshotImageInfoResolvesSegmentOffsetAndBoxForAnAiDetectionSpan()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var segmentStart = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = segmentStart, EndUtc = segmentStart.AddSeconds(60), DurationMs = 60_000,
            Width = 1920, Height = 1080, FilePath = "a.mp4"
        });
        var span = new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.AiDetection,
            StartUtc = segmentStart.AddSeconds(5), EndUtc = segmentStart.AddSeconds(10), Score = 0.9,
            BestFrameAtUtc = segmentStart.AddSeconds(8), BestBoxX = 0.3, BestBoxY = 0.4, BestBoxW = 0.2, BestBoxH = 0.25
        };
        db.MotionSpans.Add(span);
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var info = await service.GetSnapshotImageInfoAsync(cameraId, span.Id);

        Assert.NotNull(info);
        Assert.Equal("a.mp4", info!.FilePath);
        Assert.Equal(8, info.OffsetSeconds); // BestFrameAtUtc is 8s into the segment
        Assert.Equal(span.Id, info.SpanId);
        Assert.Equal(0.3, info.BoxX);
        Assert.Equal(0.4, info.BoxY);
        Assert.Equal(0.2, info.BoxW);
        Assert.Equal(0.25, info.BoxH);
        Assert.Equal(1920, info.FrameWidth);
        Assert.Equal(1080, info.FrameHeight);
        Assert.Equal("10.0.0.5", info.NodeIp);
    }

    [Fact]
    public async Task GetSnapshotImageInfoFallsBackToStartUtcWhenBestFrameAtUtcIsNull()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var segmentStart = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = segmentStart, EndUtc = segmentStart.AddSeconds(60), DurationMs = 60_000,
            Width = 1920, Height = 1080, FilePath = "a.mp4"
        });
        var span = new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.AiDetection,
            StartUtc = segmentStart.AddSeconds(12), EndUtc = segmentStart.AddSeconds(15), Score = 0.9,
            BestFrameAtUtc = null, BestBoxX = 0.1, BestBoxY = 0.1, BestBoxW = 0.1, BestBoxH = 0.1
        };
        db.MotionSpans.Add(span);
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var info = await service.GetSnapshotImageInfoAsync(cameraId, span.Id);

        Assert.NotNull(info);
        Assert.Equal(12, info!.OffsetSeconds); // falls back to StartUtc, 12s into the segment
    }

    [Fact]
    public async Task GetSnapshotImageInfoReturnsNullWhenSpanHasNoCapturedBox()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var segmentStart = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = segmentStart, EndUtc = segmentStart.AddSeconds(60), DurationMs = 60_000,
            Width = 1920, Height = 1080, FilePath = "a.mp4"
        });
        // A plain (non-AI) span — no BestBox* fields ever populated.
        var span = new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.ServerMotion,
            StartUtc = segmentStart.AddSeconds(5), EndUtc = segmentStart.AddSeconds(10), Score = 0.9
        };
        db.MotionSpans.Add(span);
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var info = await service.GetSnapshotImageInfoAsync(cameraId, span.Id);

        Assert.Null(info);
    }

    [Fact]
    public async Task GetSnapshotImageInfoReturnsNullWhenSegmentHasNoReportedResolution()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var segmentStart = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = segmentStart, EndUtc = segmentStart.AddSeconds(60), DurationMs = 60_000,
            Width = null, Height = null, FilePath = "a.mp4"
        });
        var span = new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.AiDetection,
            StartUtc = segmentStart.AddSeconds(5), EndUtc = segmentStart.AddSeconds(10), Score = 0.9,
            BestFrameAtUtc = segmentStart.AddSeconds(8), BestBoxX = 0.3, BestBoxY = 0.4, BestBoxW = 0.2, BestBoxH = 0.25
        };
        db.MotionSpans.Add(span);
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var info = await service.GetSnapshotImageInfoAsync(cameraId, span.Id);

        Assert.Null(info);
    }

    [Fact]
    public async Task GetSnapshotImageInfoReturnsNullWhenSpanBelongsToADifferentCamera()
    {
        var (db, cameraId, nodeId) = await SeedCameraAsync();
        var otherCameraId = Guid.NewGuid();
        var segmentStart = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
        db.Segments.Add(new Segment
        {
            CameraId = cameraId, NodeId = nodeId, StreamRole = CameraStreamRole.Main,
            StartUtc = segmentStart, EndUtc = segmentStart.AddSeconds(60), DurationMs = 60_000,
            Width = 1920, Height = 1080, FilePath = "a.mp4"
        });
        var span = new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.AiDetection,
            StartUtc = segmentStart.AddSeconds(5), EndUtc = segmentStart.AddSeconds(10), Score = 0.9,
            BestFrameAtUtc = segmentStart.AddSeconds(8), BestBoxX = 0.3, BestBoxY = 0.4, BestBoxW = 0.2, BestBoxH = 0.25
        };
        db.MotionSpans.Add(span);
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        // Asking for this span under the wrong camera must not leak it.
        var info = await service.GetSnapshotImageInfoAsync(otherCameraId, span.Id);

        Assert.Null(info);
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

    // ── GetSnapshotsAsync (M18: Snapshots browser) ───────────────────────────

    [Fact]
    public async Task SnapshotWithAZoneUsesTheZonesNameAndThePlainMotionColor()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var zone = new Zone { Id = Guid.NewGuid(), CameraId = cameraId, Name = "Front Yard", Kind = ZoneKind.ServerMotion };
        db.Zones.Add(zone);
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.ServerMotion, ZoneId = zone.Id,
            StartUtc = start, EndUtc = start.AddSeconds(30), Score = 0.5
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        var s = Assert.Single(page.Items);
        Assert.Equal("Front Yard", s.Label);
        Assert.Equal(EventColors.DefaultMotion, s.ColorHex);
        Assert.Equal("cam-1", s.CameraName);
    }

    [Fact]
    public async Task SnapshotWithACustomTagUsesTheRulesNameAndColor()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var rule = new EventTagRule
        {
            Id = Guid.NewGuid(), CameraId = cameraId, Name = "Package", StartTopic = "tns1:Custom/Start",
            ColorHex = "#ff9800", DrivesRecording = false, IsEnabled = true
        };
        db.EventTagRules.Add(rule);
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.CustomTag, EventTagRuleId = rule.Id,
            StartUtc = start, EndUtc = start.AddSeconds(30), Score = 1.0
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        var s = Assert.Single(page.Items);
        Assert.Equal("Package", s.Label);
        Assert.Equal("#ff9800", s.ColorHex);
    }

    [Fact]
    public async Task DetectionSnapshotSamplesOneSecondAfterStartIgnoringPreRoll()
    {
        // A classified detection (Person/Vehicle/Face/Animal/Object) fires the instant the camera's
        // own classifier confirms the subject — it's already on-frame right at StartUtc, so this
        // samples StartUtc + 1s regardless of the camera's configured pre-roll (10s default here, via
        // the 2-arg TimelineService constructor with no real settings resolver — proves pre-roll is
        // genuinely ignored for a classified span, not just defaulted to something that happens to
        // net out the same). Reaching back into the pre-roll buffer instead risks landing before the
        // subject entered frame — confirmed live as empty-scene thumbnails once that was tried. See
        // GetSnapshotsAsync's own comment.
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Vehicle,
            StartUtc = start, EndUtc = start.AddSeconds(30), Score = 1.0
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        Assert.Equal(start.AddSeconds(1), Assert.Single(page.Items).AtUtc);
    }

    [Fact]
    public async Task PlainMotionSnapshotAlsoSamplesFromTheStartOfThePreRollNotTheMidpoint()
    {
        // Explicit user ask: plain motion gets the exact same treatment as a detection now — a
        // camera-pushed motion event can have the same cooldown-inflated span length a detection
        // does, so the span's own midpoint is no more reliable there either.
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.ServerMotion,
            StartUtc = start, EndUtc = start.AddSeconds(30), Score = 0.5
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        Assert.Equal(start.AddSeconds(-9), Assert.Single(page.Items).AtUtc);
    }

    [Fact]
    public async Task SnapshotOffsetUsesTheCamerasOwnConfiguredPreRoll()
    {
        // The exact scenario reported live: a 3s pre-roll should land the snapshot 2s before
        // StartUtc (3s pre-roll minus the 1s margin lands the candidate 2s early), not at some
        // hardcoded value — confirms the per-camera setting is actually read, not just the default.
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.ServerMotion,
            StartUtc = start, EndUtc = start.AddSeconds(30), Score = 0.5
        });
        await db.SaveChangesAsync();

        var settingsResolver = new StubSettingsResolver(
            intValues: new Dictionary<string, int> { ["Recording.MotionPreRollSeconds"] = 3 });
        var service = new TimelineService(db, DefaultPalette, settingsResolver);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        Assert.Equal(start.AddSeconds(-2), Assert.Single(page.Items).AtUtc);
    }

    [Fact]
    public async Task ASpanShorterThanThePreRollOffsetStillClampsToItsOwnEnd()
    {
        // Defensive upper clamp: a detection's start+1s candidate lands past this span's own 400ms
        // end — must clamp to EndUtc rather than seek past content that doesn't exist. The configured
        // pre-roll here is irrelevant to a DetectionKind span (see GetSnapshotsAsync) — included
        // anyway to prove that's really true, not just untested.
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human,
            StartUtc = start, EndUtc = start.AddMilliseconds(400), Score = 1.0
        });
        await db.SaveChangesAsync();

        var settingsResolver = new StubSettingsResolver(
            intValues: new Dictionary<string, int> { ["Recording.MotionPreRollSeconds"] = 0 });
        var service = new TimelineService(db, DefaultPalette, settingsResolver);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        Assert.Equal(start.AddMilliseconds(400), Assert.Single(page.Items).AtUtc);
    }

    [Fact]
    public async Task SnapshotWithADetectionUsesTheClassLabelAndPaletteColor()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human,
            StartUtc = start, EndUtc = start.AddSeconds(30), Score = 1.0
        });
        await db.SaveChangesAsync();

        var custom = new StubEventColors(new EventPalette(null, null,
            new Dictionary<DetectionKind, string> { [DetectionKind.Human] = "#123456" }));
        var service = new TimelineService(db, custom);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        var s = Assert.Single(page.Items);
        Assert.Equal("Human", s.Label);
        Assert.Equal("#123456", s.ColorHex);
        Assert.Equal("🚶", s.Emoji);
    }

    [Fact]
    public async Task SnapshotWithNoZoneRuleOrDetectionFallsBackToPlainMotionLabel()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.CameraEvent,
            StartUtc = start, EndUtc = start.AddSeconds(30), Score = 1.0
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        Assert.Equal("Motion", Assert.Single(page.Items).Label);
    }

    [Fact]
    public async Task SnapshotsAreOrderedNewestFirst()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start, EndUtc = start.AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start.AddMinutes(5), EndUtc = start.AddMinutes(5).AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start.AddMinutes(2), EndUtc = start.AddMinutes(2).AddSeconds(1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        // Default 10s pre-roll (no real settings resolver in play) minus the 1s margin: -9s off each
        // span's own StartUtc.
        Assert.Equal(
            [start.AddMinutes(5).AddSeconds(-9), start.AddMinutes(2).AddSeconds(-9), start.AddSeconds(-9)],
            page.Items.Select(i => i.AtUtc));
    }

    [Fact]
    public async Task SnapshotAtUtcSamplesFromThePreRollNotTheSpanMidpoint()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.CameraEvent,
            StartUtc = start, EndUtc = start.AddSeconds(20)
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        Assert.Equal(start.AddSeconds(-9), Assert.Single(page.Items).AtUtc);
    }

    [Fact]
    public async Task SnapshotsPageClampsToTheLastPageWhenRequestedPageIsTooHigh()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 3; i++)
        {
            db.MotionSpans.Add(new MotionSpan
            {
                CameraId = cameraId, Source = MotionSource.CameraEvent,
                StartUtc = start.AddMinutes(i), EndUtc = start.AddMinutes(i).AddSeconds(1)
            });
        }
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, page: 99, pageSize: 2);

        Assert.Equal(2, page.TotalPages);
        Assert.Equal(2, page.CurrentPage);
        Assert.Single(page.Items); // page 2 of a 3-item set at pageSize 2 has the one remaining row
    }

    [Fact]
    public async Task SnapshotsCanBeFilteredByCameraAndDateRange()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var otherCameraId = Guid.NewGuid();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start, EndUtc = start.AddSeconds(1) },
            new MotionSpan { CameraId = otherCameraId, Source = MotionSource.CameraEvent, StartUtc = start, EndUtc = start.AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start.AddDays(-10), EndUtc = start.AddDays(-10).AddSeconds(1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync([cameraId], start.AddDays(-1), start.AddDays(1), 1, 24);

        var s = Assert.Single(page.Items);
        Assert.Equal(cameraId, s.CameraId);
        Assert.Equal(start.AddSeconds(-9), s.AtUtc); // default 10s pre-roll minus the 1s margin
    }

    [Fact]
    public async Task SnapshotForADeletedCameraFallsBackToAPlaceholderName()
    {
        using var db = NewDb();
        var cameraId = Guid.NewGuid(); // never added to db.Cameras
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start, EndUtc = start.AddSeconds(1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        Assert.Equal("(deleted camera)", Assert.Single(page.Items).CameraName);
    }

    [Fact]
    public async Task SnapshotsKindsFilterNarrowsToOnlyTheRequestedTypes()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var rule = new EventTagRule
        {
            Id = Guid.NewGuid(), CameraId = cameraId, Name = "Package", StartTopic = "tns1:Custom/Start",
            ColorHex = "#ff9800", DrivesRecording = false, IsEnabled = true
        };
        db.EventTagRules.Add(rule);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start, EndUtc = start.AddSeconds(1) }, // plain motion
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human, StartUtc = start.AddMinutes(1), EndUtc = start.AddMinutes(1).AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Vehicle, StartUtc = start.AddMinutes(2), EndUtc = start.AddMinutes(2).AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CustomTag, EventTagRuleId = rule.Id, StartUtc = start.AddMinutes(3), EndUtc = start.AddMinutes(3).AddSeconds(1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24, kinds: ["Human"]);

        Assert.Equal("Human", Assert.Single(page.Items).Label);
    }

    [Fact]
    public async Task SnapshotsKindsFilterCanIncludeMotionAndCustomTagsTogether()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var rule = new EventTagRule
        {
            Id = Guid.NewGuid(), CameraId = cameraId, Name = "Package", StartTopic = "tns1:Custom/Start",
            ColorHex = "#ff9800", DrivesRecording = false, IsEnabled = true
        };
        db.EventTagRules.Add(rule);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start, EndUtc = start.AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human, StartUtc = start.AddMinutes(1), EndUtc = start.AddMinutes(1).AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CustomTag, EventTagRuleId = rule.Id, StartUtc = start.AddMinutes(3), EndUtc = start.AddMinutes(3).AddSeconds(1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24, kinds: ["Motion", TimelineService.CustomTagKindToken]);

        Assert.Equal(2, page.Items.Count);
        Assert.DoesNotContain(page.Items, i => i.Label == "Human");
    }

    [Fact]
    public async Task SnapshotsKindsFilterExcludingEverythingReturnsNoResults()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start, EndUtc = start.AddSeconds(1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        // An explicitly empty (but non-null) set narrows to nothing — distinct from null, which means
        // the filter was never touched and shows everything.
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24, kinds: ["Vehicle"]);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task SnapshotsCombinesAiCategoryAndSpecificLabelIntoOneBadge()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var category = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Vehicle", ColorHex = "#3366cc", FirstSeenUtc = DateTime.UtcNow };
        db.DetectedObjectCategories.Add(category);
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = category.Id,
            DetectedObjectLabel = "car", StartUtc = start, EndUtc = start.AddSeconds(1)
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24);

        var item = Assert.Single(page.Items);
        Assert.Equal("Vehicle — car", item.Label);
        Assert.Equal("#3366cc", item.ColorHex);
    }

    [Fact]
    public async Task SnapshotsKindsFilterAcceptsAiCategoryNameAsAToken()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var vehicle = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Vehicle", ColorHex = "#3366cc", FirstSeenUtc = DateTime.UtcNow };
        var animal = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Animal", ColorHex = "#33cc66", FirstSeenUtc = DateTime.UtcNow };
        db.DetectedObjectCategories.AddRange(vehicle, animal);
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = vehicle.Id, DetectedObjectLabel = "car", StartUtc = start, EndUtc = start.AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = animal.Id, DetectedObjectLabel = "dog", StartUtc = start.AddMinutes(1), EndUtc = start.AddMinutes(1).AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start.AddMinutes(2), EndUtc = start.AddMinutes(2).AddSeconds(1) }); // plain motion
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24, kinds: ["Vehicle"]);

        Assert.Equal("Vehicle — car", Assert.Single(page.Items).Label);
    }

    [Fact]
    public async Task GetDetectedObjectCategoriesReturnsEveryCategoryOrderedByName()
    {
        var db = NewDb();
        db.DetectedObjectCategories.AddRange(
            new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Vehicle", ColorHex = "#3366cc", FirstSeenUtc = DateTime.UtcNow },
            new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Animal", ColorHex = "#33cc66", FirstSeenUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var categories = await service.GetDetectedObjectCategoriesAsync();

        Assert.Equal(["Animal", "Vehicle"], categories.Select(c => c.Name));
    }

    [Fact]
    public async Task ExcludedLabelsFiltersOutOnlyTheSpecificLabelKeepingSiblingsIncluded()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var vehicle = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Vehicle", ColorHex = "#3366cc", FirstSeenUtc = DateTime.UtcNow };
        db.DetectedObjectCategories.Add(vehicle);
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = vehicle.Id, DetectedObjectLabel = "car", StartUtc = start, EndUtc = start.AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = vehicle.Id, DetectedObjectLabel = "truck", StartUtc = start.AddMinutes(1), EndUtc = start.AddMinutes(1).AddSeconds(1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24, excludedLabels: ["Vehicle:truck"]);

        Assert.Equal("Vehicle — car", Assert.Single(page.Items).Label);
    }

    [Fact]
    public async Task ExcludedLabelsDoesNotAffectCameraNativeSpansWithNoSpecificLabel()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = cameraId, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human,
            StartUtc = start, EndUtc = start.AddSeconds(1)
        });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page = await service.GetSnapshotsAsync(null, null, null, 1, 24, excludedLabels: ["Vehicle:truck"]);

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task GetDetectedObjectLabelsReturnsDistinctPairsOrderedByCategoryThenLabel()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var vehicle = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Vehicle", ColorHex = "#3366cc", FirstSeenUtc = DateTime.UtcNow };
        var animal = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Animal", ColorHex = "#33cc66", FirstSeenUtc = DateTime.UtcNow };
        db.DetectedObjectCategories.AddRange(vehicle, animal);
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = vehicle.Id, DetectedObjectLabel = "truck", StartUtc = start, EndUtc = start.AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = vehicle.Id, DetectedObjectLabel = "car", StartUtc = start.AddMinutes(1), EndUtc = start.AddMinutes(1).AddSeconds(1) },
            // A repeat of the same (category, label) pair — must not produce a duplicate entry.
            new MotionSpan { CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = vehicle.Id, DetectedObjectLabel = "car", StartUtc = start.AddMinutes(2), EndUtc = start.AddMinutes(2).AddSeconds(1) },
            new MotionSpan { CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = animal.Id, DetectedObjectLabel = "dog", StartUtc = start.AddMinutes(3), EndUtc = start.AddMinutes(3).AddSeconds(1) },
            // No specific label at all — must be excluded, not surfaced as a null-labeled node.
            new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human, StartUtc = start.AddMinutes(4), EndUtc = start.AddMinutes(4).AddSeconds(1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var labels = await service.GetDetectedObjectLabelsAsync();

        Assert.Equal(
            [("Animal", "dog"), ("Vehicle", "car"), ("Vehicle", "truck")],
            labels.Select(l => (l.CategoryName, l.Label)));
    }

    [Fact]
    public async Task SnapshotsPagingIsStableAcrossTiedStartUtcValues()
    {
        var (db, cameraId, _) = await SeedCameraAsync();
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        // Five spans sharing the exact same StartUtc — without a deterministic secondary sort key,
        // OFFSET/FETCH page boundaries among tied rows are undefined and could duplicate or skip rows.
        for (var i = 0; i < 5; i++)
        {
            db.MotionSpans.Add(new MotionSpan { CameraId = cameraId, Source = MotionSource.CameraEvent, StartUtc = start, EndUtc = start.AddSeconds(1), Score = i });
        }
        await db.SaveChangesAsync();

        var service = new TimelineService(db, DefaultPalette);
        var page1 = await service.GetSnapshotsAsync(null, null, null, 1, 2);
        var page2 = await service.GetSnapshotsAsync(null, null, null, 2, 2);
        var page3 = await service.GetSnapshotsAsync(null, null, null, 3, 2);

        var allIds = page1.Items.Select(i => i.Id)
            .Concat(page2.Items.Select(i => i.Id))
            .Concat(page3.Items.Select(i => i.Id))
            .ToList();
        Assert.Equal(5, allIds.Count);
        Assert.Equal(5, allIds.Distinct().Count()); // no row appeared on two pages, none missing
    }
}
