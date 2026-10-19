using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Vision.Detection;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>Object intelligence: color classification, speed traps, per-object records through
/// NodeService, the Snapshots color filter, and detection alert matching.</summary>
public class ObjectIntelligenceTests
{
    // ── Color classification ────────────────────────────────────────────────

    private static (byte R, byte G, byte B) Rgb(byte r, byte g, byte b) => (r, g, b);

    [Theory]
    [InlineData(200, 30, 30, "red")]
    [InlineData(20, 60, 200, "blue")]
    [InlineData(10, 20, 110, "blue")]   // navy
    [InlineData(30, 140, 40, "green")]
    [InlineData(240, 220, 30, "yellow")]
    [InlineData(250, 140, 0, "orange")]
    [InlineData(110, 20, 130, "purple")]
    [InlineData(255, 120, 190, "pink")]
    [InlineData(120, 70, 30, "brown")]
    [InlineData(15, 15, 15, "black")]
    [InlineData(250, 250, 250, "white")]
    [InlineData(110, 110, 110, "gray")]
    public void NamesReferenceColors(byte r, byte g, byte b, string expected)
    {
        var (l, a, bb) = ObjectColorClassifier.ToLab(r, g, b);
        Assert.Equal(expected, ObjectColorClassifier.Name(l, a, bb, vehicle: false));
    }

    [Fact]
    public void LightGrayIsSilverOnAVehicleAndGrayOtherwise()
    {
        var (l, a, b) = ObjectColorClassifier.ToLab(175, 178, 180);
        Assert.Equal("silver", ObjectColorClassifier.Name(l, a, b, vehicle: true));
        Assert.Equal("gray", ObjectColorClassifier.Name(l, a, b, vehicle: false));
    }

    [Fact]
    public void EveryNameTheClassifierCanProduceIsAKnownColor()
    {
        for (var r = 0; r < 256; r += 17)
        for (var g = 0; g < 256; g += 17)
        for (var b = 0; b < 256; b += 17)
        {
            var (l, a, bb) = ObjectColorClassifier.ToLab((byte)r, (byte)g, (byte)b);
            Assert.True(ObjectColors.IsKnown(ObjectColorClassifier.Name(l, a, bb, vehicle: true)));
            Assert.True(ObjectColors.IsKnown(ObjectColorClassifier.Name(l, a, bb, vehicle: false)));
        }
    }

    /// <summary>A BGRA frame filled with <paramref name="background"/>, with rectangles painted on.</summary>
    private static byte[] Bgra(int w, int h, (byte R, byte G, byte B) background,
        params (int X0, int Y0, int X1, int Y1, (byte R, byte G, byte B) C)[] rects)
    {
        var buf = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = background;
            foreach (var r in rects)
                if (x >= r.X0 && x < r.X1 && y >= r.Y0 && y < r.Y1) c = r.C;
            var o = (y * w + x) * 4;
            buf[o] = c.B; buf[o + 1] = c.G; buf[o + 2] = c.R; buf[o + 3] = 255;
        }
        return buf;
    }

    [Fact]
    public void ClassifiesAVehicleBoxFromABgraFrame()
    {
        var frame = Bgra(200, 100, Rgb(90, 90, 90), (40, 20, 160, 90, Rgb(190, 25, 30)));
        var result = ObjectColorClassifier.Classify(new FramePixels(frame, 200, 100, false), new PixelRect(40, 20, 160, 90), "Vehicle");
        Assert.Equal("red", result.Primary);
        Assert.Null(result.Upper);
    }

    [Fact]
    public void APersonGetsShirtAndPantsColors()
    {
        // Box 60..100 x 10..90: shirt band blue, legs black.
        var frame = Bgra(160, 100, Rgb(200, 200, 200),
            (60, 10, 100, 52, Rgb(25, 70, 200)),
            (60, 52, 100, 90, Rgb(15, 15, 15)));
        var result = ObjectColorClassifier.Classify(new FramePixels(frame, 160, 100, false), new PixelRect(60, 10, 100, 90), "Human");
        Assert.Equal("blue", result.Upper);
        Assert.Equal("black", result.Lower);
        Assert.Equal("blue", result.Primary);
    }

    [Fact]
    public void AShadowClusterDoesNotWinOverANearlyAsCommonRealColor()
    {
        // Car body green with a big dark window band — the window shouldn't make it "black".
        var frame = Bgra(100, 100, Rgb(128, 128, 128),
            (0, 0, 100, 100, Rgb(30, 140, 40)),
            (0, 40, 100, 75, Rgb(10, 10, 10)));
        var result = ObjectColorClassifier.Classify(new FramePixels(frame, 100, 100, false), new PixelRect(0, 0, 100, 100), "Vehicle");
        Assert.Equal("green", result.Primary);
    }

    [Fact]
    public void TheSceneBehindTheObjectDoesNotWin()
    {
        // A brown dog filling only part of its box, on a lawn that shows all around it and inside the box.
        var lawn = Rgb(60, 140, 50);
        var frame = Bgra(200, 200, lawn, (85, 85, 115, 115, Rgb(110, 70, 35)));
        var result = ObjectColorClassifier.Classify(new FramePixels(frame, 200, 200, false), new PixelRect(70, 70, 130, 130), "Animal");
        Assert.Equal("brown", result.Primary);
    }

    [Fact]
    public void AnObjectTheSameColorAsItsSurroundingsKeepsThatColor()
    {
        // A green object on a lawn of nearly the same green: nothing else to pick, so it stays green.
        var frame = Bgra(200, 200, Rgb(60, 140, 50), (70, 70, 130, 130, Rgb(70, 150, 55)));
        var result = ObjectColorClassifier.Classify(new FramePixels(frame, 200, 200, false), new PixelRect(70, 70, 130, 130), "Animal");
        Assert.Equal("green", result.Primary);
    }

    [Fact]
    public void ATinyBoxHasNoColor()
    {
        var frame = Bgra(50, 50, Rgb(200, 30, 30));
        var result = ObjectColorClassifier.Classify(new FramePixels(frame, 50, 50, false), new PixelRect(10, 10, 13, 13), "Vehicle");
        Assert.Null(result.Primary);
    }

    [Fact]
    public void DetectsAMonochromeFrame()
    {
        var gray = Bgra(64, 64, Rgb(100, 100, 100), (10, 10, 40, 40, Rgb(200, 200, 200)));
        var color = Bgra(64, 64, Rgb(100, 100, 100), (10, 10, 40, 40, Rgb(200, 40, 40)));
        Assert.True(ObjectColorClassifier.IsMonochrome(new FramePixels(gray, 64, 64, false), new PixelRect(0, 0, 64, 64)));
        Assert.False(ObjectColorClassifier.IsMonochrome(new FramePixels(color, 64, 64, false), new PixelRect(0, 0, 64, 64)));
    }

    [Fact]
    public void ReadsNv12Frames()
    {
        // Solid limited-range BT.601 blue: Y=41, U=240, V=110.
        const int w = 64, h = 64;
        var buf = new byte[w * h * 3 / 2];
        Array.Fill(buf, (byte)41, 0, w * h);
        for (var i = w * h; i < buf.Length; i += 2) { buf[i] = 240; buf[i + 1] = 110; }
        var result = ObjectColorClassifier.Classify(new FramePixels(buf, w, h, true), new PixelRect(0, 0, w, h), "Vehicle");
        Assert.Equal("blue", result.Primary);
    }

    // ── Speed traps ─────────────────────────────────────────────────────────

    // Two horizontal gates across a vertical road: A at y=0.4, B at y=0.6, 10 m apart.
    private static readonly VisionSpeedTrapDto Trap = new(Guid.NewGuid(), 0.1, 0.4, 0.9, 0.4, 0.1, 0.6, 0.9, 0.6, 10);

    [Fact]
    public void TimesATrackBetweenTheGates()
    {
        var meter = new SpeedTrapMeter([Trap]);
        var t0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        SpeedMeasurement? result = null;
        // Moves 0.1 of the frame every 0.3 s (frames deliberately not landing on the gates): 0.2 of
        // the frame between gates takes 0.6 s → 10 m / 0.6 s = 60 km/h.
        for (var i = 0; i <= 10 && result is null; i++)
            result = meter.Observe(1, 0.5, 0.25 + 0.1 * i, t0.AddSeconds(0.3 * i));
        Assert.NotNull(result);
        Assert.Equal(Trap.Id, result!.TrapId);
        Assert.Equal(60, result.Kph, 0.5);
        // Crossed gate B (y = 0.6) between the frames at 0.3*3 s (y 0.55) and 0.3*4 s (y 0.65): halfway.
        Assert.Equal(t0.AddSeconds(1.05), result.AtUtc, TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void TimesEitherDirectionAndOnlyOnce()
    {
        var meter = new SpeedTrapMeter([Trap]);
        var t0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        var results = new List<SpeedMeasurement>();
        for (var i = 0; i <= 12; i++)
        {
            if (meter.Observe(7, 0.5, 0.75 - 0.05 * i, t0.AddSeconds(0.25 * i)) is { } m) results.Add(m);
        }
        // Back over the gates again: no second reading for the same track.
        for (var i = 0; i <= 12; i++)
        {
            if (meter.Observe(7, 0.5, 0.15 + 0.05 * i, t0.AddSeconds(3 + 0.25 * i)) is { } m) results.Add(m);
        }
        var only = Assert.Single(results);
        // 0.2 of the frame at 0.05 per 0.25 s → 1 s → 36 km/h.
        Assert.Equal(36, only.Kph, 0.5);
    }

    [Fact]
    public void IgnoresATrackThatMissesTheGates()
    {
        var meter = new SpeedTrapMeter([Trap]);
        var t0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i <= 10; i++)
            Assert.Null(meter.Observe(3, 0.95, 0.2 + 0.08 * i, t0.AddSeconds(0.2 * i))); // beyond the gates' ends
    }

    [Fact]
    public void RejectsAnImplausiblyQuickCrossing()
    {
        var meter = new SpeedTrapMeter([Trap]);
        var t0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(meter.Observe(1, 0.5, 0.3, t0));
        Assert.Null(meter.Observe(1, 0.5, 0.7, t0.AddSeconds(0.1))); // both gates in 0.05 s
    }

    [Fact]
    public void GateParsingRejectsMalformedOrDegenerateLines()
    {
        Assert.NotNull(SpeedTrapGates.Parse("""{"a":[[0.1,0.4],[0.9,0.4]],"b":[[0.1,0.6],[0.9,0.6]]}"""));
        Assert.Null(SpeedTrapGates.Parse("""{"a":[[0.1,0.4],[0.9,0.4]]}"""));
        Assert.Null(SpeedTrapGates.Parse("""{"a":[[0.1,0.4],[0.1,0.4]],"b":[[0.1,0.6],[0.9,0.6]]}"""));
        Assert.Null(SpeedTrapGates.Parse("""{"a":[[0.1,0.4],[1.9,0.4]],"b":[[0.1,0.6],[0.9,0.6]]}"""));
        Assert.Null(SpeedTrapGates.Parse("not json"));
        Assert.NotNull(SpeedTrapGates.Validate("""{"a":[[0.1,0.4],[0.9,0.4]],"b":[[0.1,0.6],[0.9,0.6]]}""", 0));
        Assert.Null(SpeedTrapGates.Validate("""{"a":[[0.1,0.4],[0.9,0.4]],"b":[[0.1,0.6],[0.9,0.6]]}""", 12));
    }

    // ── Per-track object book ───────────────────────────────────────────────

    [Fact]
    public void ObjectBookAsksForColorsOnceThenOnlyOnAClearlyBetterView()
    {
        var book = new TrackObjectBook();
        var t = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(book.Observe(1, "car", t, 0.1, 0.1, 0.2, 0.2, 0.8));
        book.SetColors(1, t, new ObjectColorResult("red", null, null));
        Assert.False(book.Observe(1, "car", t.AddSeconds(1), 0.1, 0.1, 0.2, 0.2, 0.8)); // same view
        Assert.False(book.Observe(1, "car", t.AddSeconds(0.2), 0.1, 0.1, 0.4, 0.4, 0.9)); // better, but too soon
        Assert.True(book.Observe(1, "car", t.AddSeconds(2), 0.1, 0.1, 0.4, 0.4, 0.9));
        Assert.Equal("red", book.ColorOf(1));
    }

    [Fact]
    public void ObjectBookReportsLiveAndEndedObjectsOfALabelUntilReleased()
    {
        var book = new TrackObjectBook();
        var t = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        book.Observe(1, "car", t, 0.1, 0.1, 0.2, 0.2, 0.8);
        book.Observe(2, "car", t, 0.5, 0.1, 0.2, 0.2, 0.7);
        book.Observe(3, "person", t, 0.3, 0.3, 0.1, 0.3, 0.9);
        book.SetSpeed(1, new SpeedMeasurement(Guid.NewGuid(), 42, t));

        book.Prune(new HashSet<int> { 2, 3 }, t.AddSeconds(1)); // track 1 ended
        var cars = book.ForLabel("car", t.AddSeconds(-5));
        Assert.Equal([1, 2], cars.Select(o => o.TrackId).Order());
        Assert.Equal(42, cars.Single(o => o.TrackId == 1).SpeedKph);

        book.ReleaseFinished("car");
        Assert.Equal([2], book.ForLabel("car", t.AddSeconds(-5)).Select(o => o.TrackId));
        Assert.Empty(book.ForLabel("car", t.AddSeconds(5))); // nothing seen since this span began
    }

    [Fact]
    public void ObjectBookKeepsASpeedMeasuredBeforeTheObjectWasSeenMoving()
    {
        var book = new TrackObjectBook();
        var t = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        book.SetSpeed(7, new SpeedMeasurement(Guid.NewGuid(), 55, t));
        Assert.Null(book.SpeedOf(7));

        book.Observe(7, "car", t.AddSeconds(0.2), 0.1, 0.1, 0.2, 0.2, 0.8);
        Assert.Equal(55, book.SpeedOf(7));
    }

    [Fact]
    public void ObjectBookForgetsAPendingSpeedWhenItsTrackEnds()
    {
        var book = new TrackObjectBook();
        var t = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
        book.SetSpeed(7, new SpeedMeasurement(Guid.NewGuid(), 55, t));
        book.Prune(new HashSet<int>(), t.AddSeconds(1));

        book.Observe(7, "car", t.AddSeconds(2), 0.1, 0.1, 0.2, 0.2, 0.8); // a later track reusing the id
        Assert.Null(book.SpeedOf(7));
    }

    // ── NodeService: per-object records and detection events ────────────────

    private sealed class CapturingSink : IDetectionEventSink
    {
        public List<DetectionEvent> Events { get; } = [];
        public List<MotionEvent> Motions { get; } = [];
        public void Publish(DetectionEvent detection) => Events.Add(detection);
        public void PublishMotion(MotionEvent motion) => Motions.Add(motion);
    }

    [Fact]
    public async Task OnlyPlainMotionSpansArePublishedAsMotion()
    {
        var (db, cameraId, nodeId) = await SeedAsync();
        var sink = new CapturingSink();
        var service = new NodeService(db, new SettingsResolver(db), detectionEvents: sink);
        var zoneId = Guid.NewGuid();

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, zoneId, Start, Start.AddSeconds(2), 0.5),                 // server motion
            new MotionSpanReportItem(cameraId, null, Start, Start.AddSeconds(2), 0.5),                   // camera-pushed motion
            new MotionSpanReportItem(cameraId, null, Start, Start.AddSeconds(2), 0.5, DetectionKind: DetectionKind.Human),
            new MotionSpanReportItem(cameraId, null, Start, Start.AddSeconds(2), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car"),
        ]);

        Assert.Equal(2, sink.Motions.Count);
        Assert.Contains(sink.Motions, m => m.ZoneId == zoneId);
        Assert.Contains(sink.Motions, m => m.ZoneId is null);
        Assert.All(sink.Motions, m => Assert.Equal(cameraId, m.CameraId));
        Assert.Single(sink.Events);
    }

    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static async Task<(ApplicationDbContext Db, Guid CameraId, Guid NodeId)> SeedAsync(bool withFootage = false)
    {
        var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash", MediaSigningKey = "key", StorageRootPath = @"E:\LarisVMS\recordings" };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "Driveway", Host = "10.0.0.1",
            DeviceServiceUri = "http://10.0.0.1/onvif/device_service", NodeId = node.Id
        };
        db.AddRange(node, camera);
        if (withFootage)
        {
            db.Segments.Add(new Segment
            {
                CameraId = camera.Id, NodeId = node.Id, StreamRole = CameraStreamRole.Main,
                StartUtc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc), FilePath = "seg.mp4"
            });
        }
        await db.SaveChangesAsync();
        return (db, camera.Id, node.Id);
    }

    private static readonly DateTime Start = new(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

    private static VisionDetectedObjectItem Obj(int track, string? primary = null, string? upper = null, string? lower = null,
        double? kph = null, string label = "car") =>
        new(track, label, Start, Start.AddSeconds(3), Start.AddSeconds(1), 0.2, 0.3, 0.2, 0.2, 0.8, primary, upper, lower, kph,
            kph is null ? null : Guid.NewGuid());

    [Fact]
    public async Task ObjectsAreStoredAndMergedAcrossReportsOfOneSpan()
    {
        var (db, cameraId, nodeId) = await SeedAsync();
        var sink = new CapturingSink();
        var service = new NodeService(db, new SettingsResolver(db), detectionEvents: sink);

        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, Start, Start.AddSeconds(2), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car", Objects: [Obj(1, primary: "red"), Obj(2)])
        ]);
        // A later checkpoint of the same span: track 2 now has a color and a speed; track 1's color
        // went unknown (it must not be erased); a junk color name is ignored.
        await service.RecordMotionSpansAsync(nodeId, [
            new MotionSpanReportItem(cameraId, null, Start, Start.AddSeconds(8), 0.9,
                DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car",
                Objects: [Obj(1, primary: "chartreuse"), Obj(2, primary: "white", kph: 51.5)])
        ]);

        var span = await db.MotionSpans.Include(m => m.Objects).SingleAsync();
        Assert.Equal(2, span.Objects.Count);
        Assert.Equal("red", span.Objects.Single(o => o.TrackId == 1).PrimaryColor);
        var two = span.Objects.Single(o => o.TrackId == 2);
        Assert.Equal("white", two.PrimaryColor);
        Assert.Equal(51.5, two.SpeedKph);

        Assert.Equal(2, sink.Events.Count);
        var last = sink.Events[^1];
        Assert.Equal(span.Id, last.SpanId);
        Assert.Equal("Vehicle", last.Category);
        Assert.Contains(last.Objects, o => o.TrackId == 2 && o.SpeedKph == 51.5);
    }

    [Fact]
    public async Task NodeConfigCarriesEnabledSpeedTrapsAndTheColorSetting()
    {
        var (db, cameraId, nodeId) = await SeedAsync();
        const string gates = """{"a":[[0.1,0.4],[0.9,0.4]],"b":[[0.1,0.6],[0.9,0.6]]}""";
        db.SpeedTraps.AddRange(
            new SpeedTrap { Id = Guid.NewGuid(), CameraId = cameraId, Name = "Street", GatesJson = gates, DistanceMeters = 12 },
            new SpeedTrap { Id = Guid.NewGuid(), CameraId = cameraId, Name = "Off", GatesJson = gates, DistanceMeters = 12, IsEnabled = false },
            new SpeedTrap { Id = Guid.NewGuid(), CameraId = cameraId, Name = "Broken", GatesJson = "{}", DistanceMeters = 12 });
        await db.SaveChangesAsync();
        var settings = new SettingsResolver(db);
        await settings.SetGlobalAsync("Detection.ColorClassification", "false", "test");

        var config = await new NodeService(db, settings).GetConfigAsync(nodeId);

        Assert.False(config.ColorClassification);
        var trap = Assert.Single(config.Cameras.Single(c => c.CameraId == cameraId).SpeedTraps!);
        Assert.Equal(12, trap.DistanceMeters);
        Assert.Equal(0.4, trap.AY1);
    }

    // ── Snapshots: objects on cards and the color filter ────────────────────

    private sealed class StubEventColors : IEventColorService
    {
        public Task<EventPalette> GetAsync(CancellationToken ct = default) => Task.FromResult(EventPalette.Empty);
        public Task SaveAsync(EventPalette p, string? modifiedBy, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static async Task<ApplicationDbContext> SeedSnapshotsAsync()
    {
        var (db, cameraId, _) = await SeedAsync(withFootage: true);
        var human = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Human", ColorHex = "#abcdef", FirstSeenUtc = Start };
        var vehicle = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Vehicle", ColorHex = "#123456", FirstSeenUtc = Start };
        db.DetectedObjectCategories.AddRange(human, vehicle);
        db.MotionSpans.AddRange(
            new MotionSpan
            {
                CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = human.Id, DetectedObjectLabel = "person",
                StartUtc = Start, EndUtc = Start.AddSeconds(5),
                Objects = [new DetectedObject { TrackId = 1, Label = "person", UpperColor = "blue", LowerColor = "black", PrimaryColor = "blue" }]
            },
            new MotionSpan
            {
                CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = vehicle.Id, DetectedObjectLabel = "car",
                StartUtc = Start.AddMinutes(5), EndUtc = Start.AddMinutes(5).AddSeconds(5),
                Objects = [new DetectedObject { TrackId = 4, Label = "car", PrimaryColor = "red", SpeedKph = 48 }]
            },
            new MotionSpan
            {
                CameraId = cameraId, Source = MotionSource.AiDetection, DetectedObjectCategoryId = vehicle.Id, DetectedObjectLabel = "truck",
                StartUtc = Start.AddMinutes(10), EndUtc = Start.AddMinutes(10).AddSeconds(5)
            });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task SnapshotCardsCarryTheirObjects()
    {
        using var db = await SeedSnapshotsAsync();
        var page = await new TimelineService(db, new StubEventColors()).GetSnapshotsAsync(null, null, null, 1, 24);

        var car = page.Items.Single(i => i.Label == "Vehicle — car");
        var obj = Assert.Single(car.Objects);
        Assert.Equal("red", obj.PrimaryColor);
        Assert.Equal(48, obj.SpeedKph);
        Assert.Empty(page.Items.Single(i => i.Label == "Vehicle — truck").Objects);
    }

    [Theory]
    [InlineData(new[] { "red" }, null, null, new[] { "Vehicle — car" })]
    [InlineData(new[] { "blue", "red" }, null, null, new[] { "Human", "Vehicle — car" })]
    [InlineData(null, new[] { "blue" }, new[] { "black" }, new[] { "Human" })]
    [InlineData(null, new[] { "blue" }, new[] { "white" }, new string[0])]
    [InlineData(null, null, null, new[] { "Human", "Vehicle — car", "Vehicle — truck" })]
    public async Task ColorFilterNarrowsSnapshots(string[]? colors, string[]? shirts, string[]? pants, string[] expected)
    {
        using var db = await SeedSnapshotsAsync();
        var page = await new TimelineService(db, new StubEventColors()).GetSnapshotsAsync(null, null, null, 1, 24,
            objectFilter: new SnapshotObjectFilter(colors, shirts, pants));
        Assert.Equal(expected.Order(), page.Items.Select(i => i.Label).Order());
    }

    [Theory]
    [InlineData(40.0, null, new[] { "Vehicle — car" })]
    [InlineData(50.0, null, new string[0])]
    [InlineData(40.0, "red", new[] { "Vehicle — car" })]
    [InlineData(40.0, "blue", new string[0])] // the blue object (a person) has no speed
    public async Task SpeedFilterNarrowsSnapshotsOnTheSameObject(double minKph, string? color, string[] expected)
    {
        using var db = await SeedSnapshotsAsync();
        var page = await new TimelineService(db, new StubEventColors()).GetSnapshotsAsync(null, null, null, 1, 24,
            objectFilter: new SnapshotObjectFilter(color is null ? null : [color], null, null, minKph));
        Assert.Equal(expected.Order(), page.Items.Select(i => i.Label).Order());
    }

    // ── Detection alert matching ────────────────────────────────────────────

    private static DetectionRule Rule(AlertConditionType condition = AlertConditionType.ObjectDetected, Guid? cameraId = null,
        string? category = null, string? label = null, string? color = null,
        IReadOnlyList<(double, double)>? zone = null, bool zoneFilter = false, double? speed = null,
        DayOfWeekFlags? days = null, TimeOnly? from = null, TimeOnly? to = null) =>
        new(Guid.NewGuid(), "rule", condition, cameraId, category, label, color, zoneFilter || zone is not null, zone, speed, days, from, to);

    private static DetectionEvent Event(Guid cameraId, params DetectionEventObject[] objects) =>
        new(1, cameraId, "Vehicle", "car", Start, Start.AddSeconds(5), 0.1, 0.1, 0.2, 0.2, 0.8, objects);

    private static DetectionEventObject EvObj(string label = "car", string? primary = null, string? upper = null, string? lower = null,
        double x = 0.4, double y = 0.4, double w = 0.2, double h = 0.2) =>
        new(1, label, x, y, w, h, primary, upper, lower, null, null);

    [Fact]
    public void MatchesCameraAndCategory()
    {
        var cam = Guid.NewGuid();
        Assert.True(DetectionAlertMatcher.MatchesEvent(Rule(), Event(cam)));
        Assert.True(DetectionAlertMatcher.MatchesEvent(Rule(cameraId: cam, category: "vehicle"), Event(cam)));
        Assert.False(DetectionAlertMatcher.MatchesEvent(Rule(cameraId: Guid.NewGuid()), Event(cam)));
        Assert.False(DetectionAlertMatcher.MatchesEvent(Rule(category: "Human"), Event(cam)));
    }

    [Fact]
    public void FiltersObjectsByLabelColorAndZone()
    {
        var e = Event(Guid.NewGuid(), EvObj(primary: "red"), EvObj(label: "truck", primary: "white"), EvObj(label: "person", upper: "blue", lower: "black"));
        Assert.Single(DetectionAlertMatcher.MatchingObjects(Rule(label: "truck"), e));
        Assert.Single(DetectionAlertMatcher.MatchingObjects(Rule(color: "red"), e));
        Assert.Single(DetectionAlertMatcher.MatchingObjects(Rule(color: "black"), e)); // pants count
        Assert.Empty(DetectionAlertMatcher.MatchingObjects(Rule(color: "green"), e));

        // Zone covering the left half; boxes stand at x = 0.5 (bottom centre) unless moved.
        IReadOnlyList<(double, double)> leftHalf = [(0, 0), (0.55, 0), (0.55, 1), (0, 1)];
        var inZone = Event(Guid.NewGuid(), EvObj(x: 0.1), EvObj(x: 0.7));
        Assert.Single(DetectionAlertMatcher.MatchingObjects(Rule(zone: leftHalf), inZone));
        Assert.Empty(DetectionAlertMatcher.MatchingObjects(Rule(zoneFilter: true, zone: []), inZone)); // deleted zone
    }

    [Fact]
    public void AnEventWithoutObjectsStandsInForOneWithoutColors()
    {
        var e = Event(Guid.NewGuid());
        Assert.Single(DetectionAlertMatcher.MatchingObjects(Rule(label: "car"), e));
        Assert.Empty(DetectionAlertMatcher.MatchingObjects(Rule(color: "red"), e));
    }

    [Fact]
    public void ScheduleHandlesDaysAndOvernightWindows()
    {
        var monday2300 = new DateTime(2026, 10, 12, 23, 0, 0);
        var tuesday0300 = new DateTime(2026, 10, 13, 3, 0, 0);
        var tuesday1200 = new DateTime(2026, 10, 13, 12, 0, 0);
        var night = (From: new TimeOnly(22, 0), To: new TimeOnly(6, 0));

        Assert.True(DetectionAlertMatcher.InSchedule(null, null, null, tuesday1200));
        Assert.True(DetectionAlertMatcher.InSchedule(DayOfWeekFlags.Monday, night.From, night.To, monday2300));
        Assert.True(DetectionAlertMatcher.InSchedule(DayOfWeekFlags.Monday, night.From, night.To, tuesday0300)); // Monday night's tail
        Assert.False(DetectionAlertMatcher.InSchedule(DayOfWeekFlags.Tuesday, night.From, night.To, tuesday0300));
        Assert.False(DetectionAlertMatcher.InSchedule(null, night.From, night.To, tuesday1200));
        Assert.True(DetectionAlertMatcher.InSchedule(DayOfWeekFlags.Weekdays, new TimeOnly(9, 0), new TimeOnly(17, 0), tuesday1200));
        Assert.False(DetectionAlertMatcher.InSchedule(DayOfWeekFlags.Weekends, null, null, tuesday1200));
    }

    [Fact]
    public void DescribesWhatWasSeen()
    {
        Assert.Equal("Vehicle — car (red)", DetectionAlertMatcher.Describe("Vehicle", "car", EvObj(primary: "red")));
        Assert.Equal("Human (blue shirt, black pants)", DetectionAlertMatcher.Describe("Human", "person", EvObj(label: "person", upper: "blue", lower: "black")));
        Assert.Equal("Animal — dog", DetectionAlertMatcher.Describe("Animal", "dog", null));
    }

    [Fact]
    public void SpeedUnitsConvert()
    {
        Assert.Equal("50 km/h", SpeedUnits.Format(50, "kmh"));
        Assert.Equal("31 mph", SpeedUnits.Format(50, "mph"));
        Assert.Equal(50, SpeedUnits.ToKph(SpeedUnits.FromKph(50, "mph"), "mph"), 6);
    }
}
