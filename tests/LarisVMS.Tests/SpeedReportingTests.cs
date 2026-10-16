using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>Where speed-trap speeds are reported: the measurement time and camera on each object,
/// the speed log, Playback's markers, and the speed heatmap (rollup + builder).</summary>
public class SpeedReportingTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 18, 0, 0, DateTimeKind.Utc); // 11:00 PDT, a Tuesday
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Camera NewCamera(ApplicationDbContext db, string name)
    {
        var camera = new Camera
        {
            Id = Guid.NewGuid(), Name = name, Host = "h", DeviceServiceUri = "http://h/onvif/device_service",
            TimeZoneId = "Pacific Standard Time"
        };
        db.Cameras.Add(camera);
        return camera;
    }

    private static SpeedTrap NewTrap(ApplicationDbContext db, Camera camera, string name, double? limitKph = null)
    {
        var trap = new SpeedTrap { Id = Guid.NewGuid(), CameraId = camera.Id, Name = name, GatesJson = "{}", DistanceMeters = 10, SpeedLimitKph = limitKph };
        db.SpeedTraps.Add(trap);
        return trap;
    }

    private static int _track;

    /// <summary>One measured object in its own span.</summary>
    private static DetectedObject Measured(ApplicationDbContext db, Camera camera, SpeedTrap? trap, DateTime atUtc, double kph,
        string label = "car", string? color = null)
    {
        var obj = new DetectedObject
        {
            CameraId = camera.Id, TrackId = Interlocked.Increment(ref _track), Label = label, PrimaryColor = color,
            FirstSeenUtc = atUtc.AddSeconds(-3), LastSeenUtc = atUtc.AddSeconds(2),
            SpeedKph = kph, SpeedTrapId = trap?.Id, SpeedMeasuredAtUtc = atUtc
        };
        db.MotionSpans.Add(new MotionSpan
        {
            CameraId = camera.Id, Source = MotionSource.AiDetection, DetectedObjectLabel = label,
            StartUtc = atUtc.AddSeconds(-3), EndUtc = atUtc.AddSeconds(2), Objects = [obj]
        });
        return obj;
    }

    // ── Measurement time and camera ─────────────────────────────────────────

    [Fact]
    public async Task ObjectsGetTheirCameraAndTheMomentTheirSpeedWasMeasured()
    {
        using var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n", ApiKeyHash = "h", MediaSigningKey = "k" };
        var camera = NewCamera(db, "Driveway");
        camera.NodeId = node.Id;
        db.Nodes.Add(node);
        await db.SaveChangesAsync();
        var service = new NodeService(db, new SettingsResolver(db));
        var crossed = T0.AddSeconds(1.4);

        await service.RecordMotionSpansAsync(node.Id, [
            new MotionSpanReportItem(camera.Id, null, T0, T0.AddSeconds(5), 0.9, DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car",
                Objects: [new VisionDetectedObjectItem(1, "car", T0, T0.AddSeconds(4), T0.AddSeconds(2), 0.1, 0.1, 0.2, 0.2, 0.8,
                    SpeedKph: 42, SpeedTrapId: Guid.NewGuid(), SpeedMeasuredAtUtc: crossed)])
        ]);
        // A later report must not move the measurement.
        await service.RecordMotionSpansAsync(node.Id, [
            new MotionSpanReportItem(camera.Id, null, T0, T0.AddSeconds(9), 0.9, DetectedObjectCategory: "Vehicle", DetectedObjectLabel: "car",
                Objects: [new VisionDetectedObjectItem(1, "car", T0, T0.AddSeconds(8), T0.AddSeconds(2), 0.1, 0.1, 0.2, 0.2, 0.8,
                    SpeedKph: 60, SpeedMeasuredAtUtc: T0.AddSeconds(7))])
        ]);

        var obj = await db.DetectedObjects.SingleAsync();
        Assert.Equal(camera.Id, obj.CameraId);
        Assert.Equal(42, obj.SpeedKph);
        Assert.Equal(crossed, obj.SpeedMeasuredAtUtc);
    }

    // ── Speed log ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SpeedLogListsNewestFirstWithTrapNamesLimitsAndCameraAccess()
    {
        using var db = NewDb();
        var driveway = NewCamera(db, "Driveway");
        var street = NewCamera(db, "Street cam");
        var trap = NewTrap(db, driveway, "Eastbound", limitKph: 40);
        Measured(db, driveway, trap, T0, 35, color: "red");
        Measured(db, driveway, trap, T0.AddMinutes(5), 52, label: "truck");
        var gone = new SpeedTrap { Id = Guid.NewGuid() }; // deleted: never saved
        Measured(db, street, gone, T0.AddMinutes(10), 61);
        await db.SaveChangesAsync();

        var (all, total) = await SpeedLogQuery.RunAsync(db, null, 1, 10, null, null, true);
        Assert.Equal(3, total);
        Assert.Equal([61, 52, 35], all.Select(r => r.SpeedKph));
        Assert.Equal("(deleted trap)", all[0].TrapName);
        Assert.True(all[1].OverLimit);
        Assert.False(all[2].OverLimit);
        Assert.Equal("red", all[2].Color);

        var (onlyDriveway, _) = await SpeedLogQuery.RunAsync(db, new HashSet<Guid> { driveway.Id }, 1, 10, null, null, true);
        Assert.All(onlyDriveway, r => Assert.Equal(driveway.Id, r.CameraId));
        Assert.Empty((await SpeedLogQuery.RunAsync(db, new HashSet<Guid>(), 1, 10, null, null, true)).Rows);

        var (filtered, filteredTotal) = await SpeedLogQuery.RunAsync(db, null, 1, 10, "truck", null, true);
        Assert.Equal(1, filteredTotal);
        Assert.Equal("truck", filtered.Single().Label);

        var (slowest, _) = await SpeedLogQuery.RunAsync(db, null, 1, 1, null, "speed", false);
        Assert.Equal(35, slowest.Single().SpeedKph);
        var (page2, _) = await SpeedLogQuery.RunAsync(db, null, 2, 2, null, null, true);
        Assert.Equal(35, page2.Single().SpeedKph);
    }

    // ── Playback markers ────────────────────────────────────────────────────

    private sealed class StubEventColors : IEventColorService
    {
        public Task<EventPalette> GetAsync(CancellationToken ct = default) => Task.FromResult(EventPalette.Empty);
        public Task SaveAsync(EventPalette p, string? modifiedBy, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task SpeedMarkersCoverOneCameraInRangeInTheConfiguredUnit()
    {
        using var db = NewDb();
        var cam = NewCamera(db, "Driveway");
        var other = NewCamera(db, "Other");
        var trap = NewTrap(db, cam, "Street", limitKph: 50);
        Measured(db, cam, trap, T0, 80.5, color: "blue");
        Measured(db, cam, trap, T0.AddHours(3), 30);   // out of range
        Measured(db, other, null, T0, 99);              // other camera
        var settings = new SettingsResolver(db);
        await db.SaveChangesAsync();
        await settings.SetGlobalAsync(SpeedUnits.SettingKey, "mph", "test");

        var markers = await new TimelineService(db, new StubEventColors(), settings)
            .GetSpeedMarkersAsync(cam.Id, T0.AddHours(-1), T0.AddHours(1));

        var m = Assert.Single(markers);
        Assert.Equal("50 mph", m.SpeedText);
        Assert.True(m.OverLimit);
        Assert.Equal("blue", m.Color);
        Assert.Equal("Street", m.TrapName);
        Assert.Equal(T0, m.AtUtc);
    }

    // ── Speed heatmap: rollup ───────────────────────────────────────────────

    [Fact]
    public async Task RollupStoresEachHourOnceAndKeepsItAfterTheObjectsAreGone()
    {
        using var db = NewDb();
        var cam = NewCamera(db, "Driveway");
        var trap = NewTrap(db, cam, "Street", limitKph: 40);
        Measured(db, cam, trap, T0.AddMinutes(10), 30);
        Measured(db, cam, trap, T0.AddMinutes(20), 50);
        await db.SaveChangesAsync();

        var rollup = new SpeedRollupService(db);
        await rollup.RollupAsync(T0.AddHours(2));
        await rollup.RollupAsync(T0.AddHours(3)); // re-roll overlaps the same hour

        var stat = await db.SpeedHourlyStats.SingleAsync();
        Assert.Equal(2, stat.Count);
        Assert.Equal(80, stat.SumKph);
        Assert.Equal(50, stat.MaxKph);
        Assert.Equal(1, stat.OverLimitCount);
        Assert.Equal(T0, stat.HourUtc);

        db.MotionSpans.RemoveRange(db.MotionSpans);
        db.DetectedObjects.RemoveRange(db.DetectedObjects);
        await db.SaveChangesAsync();
        await rollup.RollupAsync(T0.AddHours(4));
        Assert.Equal(2, (await db.SpeedHourlyStats.SingleAsync()).Count);
    }

    [Fact]
    public async Task HeatmapCombinesHistoryAndTheLiveTailWithoutCountingTwice()
    {
        using var db = NewDb();
        var cam = NewCamera(db, "Driveway");
        var trap = NewTrap(db, cam, "Street");
        var now = DateTime.UtcNow;
        Measured(db, cam, trap, now.AddHours(-30), 40); // rolled up
        Measured(db, cam, trap, now.AddMinutes(-5), 60); // live tail
        await db.SaveChangesAsync();
        await new SpeedRollupService(db).RollupAsync(now);

        var service = new ActivityService(db, new ActivityRollupService(db), new MemoryCache(new MemoryCacheOptions()));
        var heatmap = await service.GetSpeedHeatmapAsync(cam.Id, null, "7d", null, "avg", null);

        Assert.NotNull(heatmap);
        Assert.Equal(2, heatmap!.Measured);
        Assert.Equal(50, heatmap.Average);
        Assert.Equal(60, heatmap.Top);
        Assert.Single(heatmap.Traps);
        Assert.Null(await service.GetSpeedHeatmapAsync(cam.Id, new HashSet<Guid>(), "7d", null, "avg", null));
    }

    // ── Speed heatmap: builder ──────────────────────────────────────────────

    private static readonly Guid TrapA = Guid.NewGuid(), TrapB = Guid.NewGuid();
    private static readonly Dictionary<Guid, (string Name, double? LimitKph)> Traps = new()
    {
        [TrapA] = ("Eastbound", 40),
        [TrapB] = ("Westbound", 40),
    };

    private static SpeedHourStat Stat(DateTime hourUtc, Guid trap, int count, double sum, double max, int over = 0) =>
        new(hourUtc, trap, "car", count, sum, max, over);

    [Fact]
    public void AveragesAreTrueAveragesAndTopIsTheFastest()
    {
        var now = T0.AddDays(2);
        // Tuesday 11:00 PDT: one hour of 2 at 30 avg, another trap the same hour with 1 at 60.
        var stats = new[] { Stat(T0, TrapA, 2, 60, 35), Stat(T0, TrapB, 1, 60, 60, over: 1) };

        var avg = SpeedHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", now, stats, Traps, null, "avg", "kmh");
        Assert.Equal("week", avg.Mode);
        var row = avg.RowLabels.IndexOf("Tue");
        Assert.Equal(40, avg.Cells[row][11]);         // (60 + 60) / 3, not (30 + 60) / 2
        Assert.Equal(3, avg.Measured);
        Assert.Equal(40, avg.Limit);                  // both traps share it
        Assert.Contains("Tuesdays", avg.Peak);

        var top = SpeedHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", now, stats, Traps, null, "max", "mph");
        Assert.Equal(37.3, top.Cells[row][11]);       // 60 km/h in mph
        Assert.Equal("mph", top.Unit);

        var over = SpeedHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", now, stats, Traps, null, "over", "kmh");
        Assert.Equal(1, over.Cells[row][11]);
        Assert.Null(over.Cells[row][10]);             // nothing measured
    }

    [Fact]
    public void TrapSelectionNarrowsAndUnknownSelectionMeansAll()
    {
        var stats = new[] { Stat(T0, TrapA, 2, 60, 35), Stat(T0, TrapB, 1, 60, 60) };
        var onlyB = SpeedHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", T0.AddDays(1), stats, Traps, [TrapB.ToString()], "count", "kmh");
        Assert.Equal(1, onlyB.Measured);
        Assert.Equal([TrapB.ToString()], onlyB.Selection);

        var unknown = SpeedHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", T0.AddDays(1), stats, Traps, ["nope"], "count", "kmh");
        Assert.Equal(3, unknown.Measured);
        Assert.Empty(unknown.Selection);
    }

    [Fact]
    public void CalendarViewFoldsIntoLocalDatesWithDailyValues()
    {
        // 06:30 UTC on the 14th is 23:30 PDT on the 13th.
        var late = new DateTime(2026, 10, 14, 6, 0, 0, DateTimeKind.Utc);
        var stats = new[] { Stat(late, TrapA, 1, 70, 70), Stat(T0, TrapA, 1, 30, 30) };
        var map = SpeedHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "30d", T0.AddDays(5), stats, Traps, null, "max", "kmh", view: "calendar");

        Assert.Equal("calendar", map.Mode);
        var col = map.ColumnLabels.IndexOf("2026-10-13");
        Assert.Equal(70, map.Cells[23][col]);
        Assert.Equal(30, map.Cells[11][col]);
        Assert.Equal(70, map.DayValues![col]);
        Assert.Equal(30, map.Min);
    }

    [Fact]
    public void DayViewHasOneRowPerTrap()
    {
        var now = T0.AddMinutes(30);
        var stats = new[] { Stat(T0, TrapA, 1, 30, 30), Stat(T0.AddHours(-2), TrapB, 1, 45, 45), Stat(T0.AddDays(-3), TrapB, 1, 99, 99) };
        var map = SpeedHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "24h", now, stats, Traps, null, "max", "kmh");

        Assert.Equal("day", map.Mode);
        Assert.Equal(["Eastbound", "Westbound"], map.RowLabels);
        Assert.Equal(30, map.Cells[0][23]);
        Assert.Equal(45, map.Cells[1][21]);
        Assert.DoesNotContain(99.0, map.Cells.SelectMany(r => r).OfType<double>());
    }
}
