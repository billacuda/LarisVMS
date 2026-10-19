using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Media;
using LarisVMS.Vision.Detection;

namespace LarisVMS.Tests;

/// <summary>Scene heatmaps: object paths recorded in Vision, stored with the event, drawn into hourly
/// grids by the rollup; grid motion counted on the node and reported per run; and the read that adds
/// them up for a range and time of day.</summary>
public class SceneHeatmapTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // ── Shared pieces ─────────────────────────────────────────────────────

    [Fact]
    public void APathSurvivesTextAndMalformedPairsAreSkipped()
    {
        var text = SceneHeat.FormatPath([(0.1f, 0.2f), (0.5f, 0.55f), (1.4f, -0.2f)]);
        Assert.Equal("0.1,0.2;0.5,0.55;1,0", text);
        Assert.Equal([(0.1f, 0.2f), (0.5f, 0.55f), (1f, 0f)], SceneHeat.ParsePath(text + ";oops;3"));
    }

    [Fact]
    public void APathCountsEachCellItCrossesOnce()
    {
        // Left to right across a 10x10 grid, then back over the same cells.
        var cells = SceneHeat.PathCells([(0.05f, 0.55f), (0.95f, 0.55f), (0.05f, 0.55f)], 10, 10);
        Assert.Equal(10, cells.Count);
        Assert.All(cells, c => Assert.Equal(5, c / 10));
    }

    [Fact]
    public void ADiagonalJumpStillLeavesAContinuousTrail()
    {
        // Two points far apart (a fast object seen twice): every cell along the diagonal counts.
        var cells = SceneHeat.PathCells([(0.05f, 0.05f), (0.95f, 0.95f)], 10, 10);
        for (var i = 0; i < 10; i++) Assert.Contains(i * 10 + i, cells);
    }

    [Fact]
    public void OnlyNonZeroCellsAreStoredAndTheyAddUp()
    {
        var counts = new int[64 * 36];
        counts[0] = 3;
        counts[100] = 70000;
        var encoded = SceneHeat.EncodeCells(counts);
        Assert.Equal(12, encoded.Length);

        var sum = new long[counts.Length];
        SceneHeat.AddCells(encoded, sum);
        SceneHeat.AddCells(encoded, sum);
        Assert.Equal(6, sum[0]);
        Assert.Equal(140000, sum[100]);
    }

    [Fact]
    public void AGridOfAnotherSizeLandsOnTheSameSpot()
    {
        var small = new int[4 * 4];
        small[1 * 4 + 3] = 5; // right edge, second row
        var into = new long[8 * 8];
        SceneHeat.AddCellsResampled(SceneHeat.EncodeCells(small), 4, 4, into, 8, 8);
        Assert.Equal(5, into[3 * 8 + 7]);
    }

    // ── Recording paths ───────────────────────────────────────────────────

    [Fact]
    public void APathIsRecordedOnlyWhileMovingAndThinsWhenFull()
    {
        var book = new TrackObjectBook();
        // Idle sightings add nothing.
        book.Observe(1, "car", T0, 0.1, 0.1, 0.1, 0.1, 0.8, moving: false);
        book.Observe(1, "car", T0.AddSeconds(1), 0.1, 0.1, 0.1, 0.1, 0.8, moving: false);
        Assert.Null(book.ForLabel("car", T0).Single().Path);

        // Moving: a point per step past the threshold; tiny wobble adds none.
        for (var i = 0; i < 300; i++)
            book.Observe(1, "car", T0.AddSeconds(2 + i * 0.1), 0.1 + i * 0.002, 0.1, 0.1, 0.1, 0.8);
        book.Observe(1, "car", T0.AddSeconds(40), 0.1 + 299 * 0.002 + 0.001, 0.1, 0.1, 0.1, 0.8);
        var path = SceneHeat.ParsePath(book.ForLabel("car", T0).Single().Path);
        Assert.InRange(path.Count, 2, TrackObjectBook.MaxPathPoints);
        // It ends where the object ended, give or take one step (the last tiny move adds no point).
        var finalX = 0.1f + 299 * 0.002f + 0.05f;
        Assert.InRange(path[^1].X, finalX - TrackObjectBook.PathStep - 0.001f, finalX + 0.001f);
    }

    [Fact]
    public async Task TheServerKeepsTheLongerPathAndStoresOnlyWhatItCanParse()
    {
        using var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n", ApiKeyHash = "h", MediaSigningKey = "k" };
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Yard", Host = "h", DeviceServiceUri = "http://h/onvif", NodeId = node.Id };
        db.Nodes.Add(node);
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();
        var service = new NodeService(db, new SettingsResolver(db));

        MotionSpanReportItem Report(string? path) => new(camera.Id, null, T0, T0.AddSeconds(5), 0.9, DetectedObjectCategory: "Human", DetectedObjectLabel: "person",
            Objects: [new VisionDetectedObjectItem(1, "person", T0, T0.AddSeconds(4), T0.AddSeconds(2), 0.1, 0.1, 0.2, 0.2, 0.8, Path: path)]);

        await service.RecordMotionSpansAsync(node.Id, [Report("0.1,0.1;0.2,0.2;0.3,0.3")]);
        await service.RecordMotionSpansAsync(node.Id, [Report("0.1,0.1;junk")]);
        Assert.Equal("0.1,0.1;0.2,0.2;0.3,0.3", (await db.DetectedObjects.SingleAsync()).Path);
    }

    // ── Rollup and read ───────────────────────────────────────────────────

    private static async Task<Camera> CameraWithPathsAsync(ApplicationDbContext db)
    {
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Yard", Host = "h", DeviceServiceUri = "http://h/onvif", TimeZoneId = "UTC" };
        db.Cameras.Add(camera);
        void Walk(string label, DateTime at, string path)
        {
            db.MotionSpans.Add(new MotionSpan
            {
                CameraId = camera.Id, Source = MotionSource.AiDetection, DetectedObjectLabel = label,
                StartUtc = at, EndUtc = at.AddSeconds(5),
                Objects = [new DetectedObject { CameraId = camera.Id, TrackId = Random.Shared.Next(), Label = label, FirstSeenUtc = at, LastSeenUtc = at.AddSeconds(5), Path = path }],
            });
        }
        // Two people along the same line at 18:xx, a car at 19:xx elsewhere.
        Walk("person", T0.AddMinutes(5), "0.05,0.5;0.95,0.5");
        Walk("person", T0.AddMinutes(20), "0.05,0.5;0.95,0.5");
        Walk("car", T0.AddHours(1).AddMinutes(5), "0.05,0.9;0.95,0.9");
        await db.SaveChangesAsync();
        return camera;
    }

    [Fact]
    public async Task TheRollupCountsEachObjectOncePerCellByHourAndCategory()
    {
        using var db = NewDb();
        var camera = await CameraWithPathsAsync(db);
        await new SceneHeatRollupService(db).RollupAsync(T0.AddHours(3));

        var rows = await db.SceneHeatHourlies.Where(r => r.CameraId == camera.Id).ToListAsync();
        var people = rows.Single(r => r.Label == CocoCategoryMap.Human);
        var cars = rows.Single(r => r.Label == CocoCategoryMap.Vehicle);
        Assert.Equal(T0, people.HourUtc);
        Assert.Equal(T0.AddHours(1), cars.HourUtc);

        var cells = new long[people.Cols * people.Rows];
        SceneHeat.AddCells(people.Cells, cells);
        Assert.Equal(2, cells.Max());

        // Re-rolling with nothing new changes nothing; history stays when the events are gone.
        db.MotionSpans.RemoveRange(db.MotionSpans);
        await db.SaveChangesAsync();
        await new SceneHeatRollupService(db).RollupAsync(T0.AddHours(4));
        Assert.Equal(2, await db.SceneHeatHourlies.CountAsync());
    }

    [Fact]
    public async Task TheReadAddsTheRangeUpNarrowedByCategoryAndTimeOfDay()
    {
        using var db = NewDb();
        var camera = await CameraWithPathsAsync(db);
        await new SceneHeatRollupService(db).RollupAsync(T0.AddHours(3));
        var activity = new ActivityService(db, new ActivityRollupService(db), new MemoryCache(new MemoryCacheOptions()));

        var all = await activity.GetSceneHeatAsync(camera.Id, null, "7d", "objects", null, null, null);
        Assert.NotNull(all);
        Assert.Equal(2, all!.MaxCount);
        Assert.Equal(1.0, all.Cells.Max());

        var cars = await activity.GetSceneHeatAsync(camera.Id, null, "7d", "objects", [CocoCategoryMap.Vehicle], null, null);
        Assert.Equal(1, cars!.MaxCount);

        var evenings = await activity.GetSceneHeatAsync(camera.Id, null, "7d", "objects", null, 19, 20);
        Assert.Equal(1, evenings!.MaxCount);

        Assert.Null(await activity.GetSceneHeatAsync(camera.Id, new HashSet<Guid>(), "7d", "objects", null, null, null));
    }

    [Theory]
    [InlineData(5, null, null, true)]
    [InlineData(5, 8, 17, false)]
    [InlineData(9, 8, 17, true)]
    [InlineData(23, 22, 6, true)]
    [InlineData(3, 22, 6, true)]
    [InlineData(12, 22, 6, false)]
    public void TimeOfDayWrapsPastMidnight(int hour, int? from, int? to, bool included) =>
        Assert.Equal(included, ActivityService.InHours(hour, from, to));

    // ── Grid motion ───────────────────────────────────────────────────────

    [Fact]
    public void MotionHeatCountsCellsOverTheThresholdAndSkipsMaskedOnes()
    {
        var heat = new MotionHeat(2, 0.1, new HashSet<int> { 3 });
        heat.Add(T0.AddMinutes(1), [0.5, 0.05, 0.2, 0.9]);
        heat.Add(T0.AddMinutes(2), [0.5, 0.0, 0.0, 0.9]);
        var hour = Assert.Single(heat.Take());
        Assert.Equal(T0, hour.HourUtc);
        Assert.Equal([2, 0, 1, 0], hour.Counts);

        // A new hour hands the finished one over; a failed report gives it back.
        heat.Add(T0.AddHours(1).AddMinutes(1), [1, 1, 1, 1]);
        var taken = heat.Take();
        Assert.Equal(2, taken.Count);
        heat.Return(taken.Where(h => h.HourUtc == T0));
        Assert.Equal(2, heat.Take().Count);
    }

    [Fact]
    public async Task MotionHeatReplacesItsRunsReportAndAddsRunsTogether()
    {
        using var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n", ApiKeyHash = "h", MediaSigningKey = "k" };
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = "Yard", Host = "h", DeviceServiceUri = "http://h/onvif", NodeId = node.Id, TimeZoneId = "UTC",
            ServerMotionEnabled = true, MotionRegionMode = MotionRegionMode.Grid, MotionGridSize = 16,
        };
        db.Nodes.Add(node);
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();
        var service = new NodeService(db, new SettingsResolver(db));
        var hour = ActivityRollupService.FloorHour(DateTime.UtcNow.AddHours(-1));
        string Cells(int cell, int count) { var c = new int[256]; c[cell] = count; return Convert.ToBase64String(SceneHeat.EncodeCells(c)); }

        var runA = Guid.NewGuid();
        await service.RecordMotionHeatAsync(node.Id, [new NodeMotionHeatReport(camera.Id, hour, 16, runA, Cells(10, 4))]);
        await service.RecordMotionHeatAsync(node.Id, [new NodeMotionHeatReport(camera.Id, hour, 16, runA, Cells(10, 9))]); // retry/update
        await service.RecordMotionHeatAsync(node.Id, [new NodeMotionHeatReport(camera.Id, hour, 16, Guid.NewGuid(), Cells(10, 1))]); // after a restart
        // Not this node's camera, and a nonsense grid size: dropped.
        await service.RecordMotionHeatAsync(node.Id, [new NodeMotionHeatReport(Guid.NewGuid(), hour, 16, runA, Cells(1, 1))]);
        await service.RecordMotionHeatAsync(node.Id, [new NodeMotionHeatReport(camera.Id, hour, 7, runA, Cells(1, 1))]);
        Assert.Equal(2, await db.SceneHeatHourlies.CountAsync());

        var activity = new ActivityService(db, new ActivityRollupService(db), new MemoryCache(new MemoryCacheOptions()));
        var heat = await activity.GetSceneHeatAsync(camera.Id, null, "24h", "motion", null, null, null);
        Assert.Equal(10, heat!.MaxCount);
        Assert.True(heat.MotionAvailable);
        Assert.Equal((16, 16), (heat.Cols, heat.Rows));
    }

    // An uploaded background is laid under the heat edge to edge, so only the camera's own shape fits.
    [Theory]
    [InlineData(1920, 1080, 2560, 1440, true)]   // same shape, other size
    [InlineData(1280, 720, 1920, 1080, true)]
    [InlineData(1900, 1080, 1920, 1080, true)]   // a few pixels cropped
    [InlineData(1080, 1080, 1920, 1080, false)]  // square
    [InlineData(1440, 1080, 1920, 1080, false)]  // 4:3 for a 16:9 camera
    [InlineData(1080, 1920, 1920, 1080, false)]  // portrait
    [InlineData(0, 1080, 1920, 1080, false)]
    [InlineData(1920, 1080, 0, 0, false)]
    public void UploadedBackgroundMustBeTheCamerasShape(int width, int height, int cameraWidth, int cameraHeight, bool expected) =>
        Assert.Equal(expected, LarisVMS.Web.Helpers.SceneBackground.ShapeMatches(width, height, cameraWidth, cameraHeight));

    [Fact]
    public async Task CameraShapePrefersTheMainStream()
    {
        await using var db = NewDb();
        var cameraId = Guid.NewGuid();
        db.CameraStreams.Add(new CameraStream { Id = Guid.NewGuid(), CameraId = cameraId, Role = CameraStreamRole.Sub, Width = 640, Height = 480 });
        db.CameraStreams.Add(new CameraStream { Id = Guid.NewGuid(), CameraId = cameraId, Role = CameraStreamRole.Main, Width = 2560, Height = 1440 });
        await db.SaveChangesAsync();
        Assert.Equal((2560, 1440), await SceneHeatRollupService.CameraShapeAsync(db, cameraId, default));
        Assert.Equal((0, 0), await SceneHeatRollupService.CameraShapeAsync(db, Guid.NewGuid(), default));
    }

    // An object the tracker found coming back into view is one object, not two.
    [Fact]
    public void ARejoinedTrackIsOneObject()
    {
        var book = new TrackObjectBook();
        var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        book.Observe(1, "person", t0, 0.10, 0.4, 0.05, 0.2, 0.9);
        book.Observe(1, "person", t0.AddSeconds(1), 0.20, 0.4, 0.05, 0.2, 0.9);
        book.Observe(2, "person", t0.AddSeconds(3), 0.40, 0.4, 0.05, 0.2, 0.8);
        book.Merge(2, 1);
        book.Observe(1, "person", t0.AddSeconds(4), 0.50, 0.4, 0.05, 0.2, 0.9);

        var item = Assert.Single(book.ForLabel("person", t0));
        Assert.Equal(1, item.TrackId);
        Assert.Equal(t0, item.FirstSeenUtc);
        Assert.Equal(4, SceneHeat.ParsePath(item.Path).Count);
    }

    [Fact]
    public void ColorsThatCouldBeTheSameObject()
    {
        Assert.True(ObjectColorClassifier.CouldBeSameColor("white", "silver"));
        Assert.True(ObjectColorClassifier.CouldBeSameColor("Red", "red"));
        Assert.True(ObjectColorClassifier.CouldBeSameColor(null, "blue"));
        Assert.False(ObjectColorClassifier.CouldBeSameColor("red", "blue"));
        Assert.False(ObjectColorClassifier.CouldBeSameColor("white", "black"));
    }
}
