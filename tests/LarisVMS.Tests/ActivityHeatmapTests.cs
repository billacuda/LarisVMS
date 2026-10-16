using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using Microsoft.Extensions.Caching.Memory;

namespace LarisVMS.Tests;

/// <summary>The per-camera activity heatmap: folding UTC hours into the camera's local day and hour,
/// the default and explicit selections, the 24-hour view, and the query behind it.</summary>
public class ActivityHeatmapTests
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    // Tuesday 13 Oct 2026, 20:30 UTC = 13:30 PDT.
    private static readonly DateTime Now = new(2026, 10, 13, 20, 30, 0, DateTimeKind.Utc);

    private static ActivityHourCount At(DateTime utcHour, string group, string? label, int count) => new(utcHour, group, label, count);

    [Fact]
    public void WeekViewFoldsUtcHoursIntoTheCamerasLocalDayAndHour()
    {
        // 03:00 UTC Wednesday = 20:00 PDT Tuesday.
        var counts = new[] { At(new DateTime(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc), "Human", "person", 4) };

        var map = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", Now, counts, null);

        Assert.Equal("week", map.Mode);
        Assert.Equal(["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"], map.RowLabels);
        Assert.Equal(4, map.Cells[2][20]); // Tuesday, 8 PM
        Assert.Equal(4, map.Total);
        Assert.StartsWith("Busiest: Tuesdays 8 PM–9 PM (4 events)", map.Peak);
    }

    [Fact]
    public void MotionIsOffByDefaultUnlessItsAllThereIs()
    {
        var hour = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
        var mixed = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", Now,
            [At(hour, "Animal", "cat", 2), At(hour, ActivityHeatmapBuilder.MotionGroup, null, 50)], null);
        Assert.Equal(2, mixed.Total);
        Assert.Equal(["Animal", "Motion"], mixed.Groups.Select(g => g.Key));
        Assert.Equal(["Animal"], mixed.Selection);

        var motionOnly = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", Now,
            [At(hour, ActivityHeatmapBuilder.MotionGroup, null, 50)], null);
        Assert.Equal(50, motionOnly.Total);
    }

    [Fact]
    public void ALabelCanBeSelectedOnItsOwn()
    {
        var hour = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
        var counts = new[] { At(hour, "Animal", "cat", 2), At(hour, "Animal", "dog", 5), At(hour, "Human", "person", 9) };

        var cats = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "30d", Now, counts, ["Animal:cat"]);

        Assert.Equal(2, cats.Total);
        var animal = cats.Groups.Single(g => g.Key == "Animal");
        Assert.Equal(7, animal.Count); // the tree still shows every label's own total
        Assert.Equal(["Animal:dog", "Animal:cat"], animal.Labels.Select(l => l.Key));
    }

    [Fact]
    public void DayViewHasOneRowPerTypeAndTheLast24HoursAsColumns()
    {
        var counts = new[]
        {
            At(new DateTime(2026, 10, 13, 20, 0, 0, DateTimeKind.Utc), "Human", "person", 3), // current hour
            At(new DateTime(2026, 10, 12, 21, 0, 0, DateTimeKind.Utc), "Vehicle", "car", 1),  // 23 h ago
            At(new DateTime(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc), "Vehicle", "car", 8),  // 24 h ago — out of range
        };

        var map = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "24h", Now, counts, null);

        Assert.Equal("day", map.Mode);
        Assert.Equal(["Human", "Vehicle"], map.RowLabels);
        Assert.Equal("1p", map.ColumnLabels[^1]);  // 20:00 UTC = 1 PM PDT
        Assert.Equal("2p", map.ColumnLabels[0]);
        Assert.Equal(3, map.Cells[0][23]);
        Assert.Equal(1, map.Cells[1][0]);
        Assert.Equal(4, map.Total);
    }

    [Fact]
    public async Task QueryGroupsByHourAndUsesOneSourceOfObjectClasses()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "Deck", Host = "h", DeviceServiceUri = "http://h/onvif/device_service", TimeZoneId = "Pacific Standard Time" };
        var animal = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Animal", ColorHex = "#fff" };
        db.AddRange(camera, animal);
        var start = DateTime.UtcNow.AddHours(-2);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = camera.Id, StartUtc = start, EndUtc = start.AddSeconds(5), Source = MotionSource.AiDetection, DetectedObjectCategoryId = animal.Id, DetectedObjectLabel = "cat" },
            new MotionSpan { CameraId = camera.Id, StartUtc = start.AddMinutes(1), EndUtc = start.AddMinutes(1).AddSeconds(5), Source = MotionSource.AiDetection, DetectedObjectCategoryId = animal.Id, DetectedObjectLabel = "cat" },
            // The camera's own classification of the same moment — dropped, AI detection already counts it.
            new MotionSpan { CameraId = camera.Id, StartUtc = start, EndUtc = start.AddSeconds(5), Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Animal },
            new MotionSpan { CameraId = camera.Id, StartUtc = start, EndUtc = start.AddSeconds(5), Source = MotionSource.ServerMotion });
        await db.SaveChangesAsync();

        var map = await Service(db).GetHeatmapAsync(camera.Id, null, "7d", null, null);

        Assert.NotNull(map);
        Assert.Equal(["Animal", "Motion"], map.Groups.Select(g => g.Key));
        Assert.Equal(2, map.Groups[0].Count);
        Assert.Equal(2, map.Total); // Motion is off by default
        Assert.Equal("Deck", map.CameraName);
    }

    private static ActivityService Service(ApplicationDbContext db) =>
        new(db, new ActivityRollupService(db), new MemoryCache(new MemoryCacheOptions()));

    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Camera NewCamera(ApplicationDbContext db, string name)
    {
        var c = new Camera { Id = Guid.NewGuid(), Name = name, Host = "h", DeviceServiceUri = "http://h/onvif/device_service", TimeZoneId = "Pacific Standard Time" };
        db.Add(c);
        return c;
    }

    private static MotionSpan Span(Guid camera, DateTime start, Action<MotionSpan>? set = null)
    {
        var m = new MotionSpan { CameraId = camera, StartUtc = start, EndUtc = start.AddSeconds(5), Source = MotionSource.ServerMotion };
        set?.Invoke(m);
        return m;
    }

    [Fact]
    public async Task RollupKeepsCountsAfterTheSpansAreDeletedAndReRollsIdempotently()
    {
        var db = NewDb();
        var cam = NewCamera(db, "Deck");
        var animal = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Animal", ColorHex = "#fff" };
        db.Add(animal);
        var t = ActivityRollupService.FloorHour(DateTime.UtcNow).AddDays(-60);
        db.MotionSpans.AddRange(
            Span(cam.Id, t.AddMinutes(5), m => { m.Source = MotionSource.AiDetection; m.DetectedObjectCategoryId = animal.Id; m.DetectedObjectLabel = "cat"; }),
            Span(cam.Id, t.AddMinutes(9), m => { m.Source = MotionSource.AiDetection; m.DetectedObjectCategoryId = animal.Id; m.DetectedObjectLabel = "cat"; }),
            // Same camera-hour as AI rows: the camera's own classification is dropped.
            Span(cam.Id, t.AddMinutes(5), m => { m.Source = MotionSource.CameraEvent; m.DetectionKind = DetectionKind.Animal; }),
            Span(cam.Id, t.AddHours(3)));
        await db.SaveChangesAsync();
        var rollup = new ActivityRollupService(db);

        await rollup.RollupAsync(DateTime.UtcNow);
        await rollup.RollupAsync(DateTime.UtcNow); // re-run: nothing doubles

        var rows = db.ActivityHourlyCounts.OrderBy(r => r.HourUtc).ToList();
        Assert.Equal([("Animal", "cat", 2), ("Motion", (string?)null, 1)], rows.Select(r => (r.Group, r.Label, r.Count)));

        // Retention deletes the spans; the history stays and the heatmap still shows them.
        db.MotionSpans.RemoveRange(db.MotionSpans);
        await db.SaveChangesAsync();
        var map = await Service(db).GetHeatmapAsync(cam.Id, null, "90d", null, ["Animal", "Motion"]);
        Assert.Equal(3, map!.Total);
    }

    [Fact]
    public async Task HistoryAndTheLiveTailAreNotCountedTwice()
    {
        var db = NewDb();
        var cam = NewCamera(db, "Deck");
        var now = DateTime.UtcNow;
        db.MotionSpans.AddRange(Span(cam.Id, now.AddHours(-5)), Span(cam.Id, now.AddMinutes(-1)));
        await db.SaveChangesAsync();
        await new ActivityRollupService(db).RollupAsync(now);
        db.MotionSpans.Add(Span(cam.Id, now.AddSeconds(-5))); // arrives after the rollup, in the live tail
        await db.SaveChangesAsync();

        var map = await Service(db).GetHeatmapAsync(cam.Id, null, "7d", null, ["Motion"]);

        Assert.Equal(3, map!.Total);
    }

    [Fact]
    public async Task AllCamerasSumsOnlyTheCamerasTheViewerMayReview()
    {
        var db = NewDb();
        var a = NewCamera(db, "A");
        var b = NewCamera(db, "B");
        var hidden = NewCamera(db, "Hidden");
        var now = DateTime.UtcNow;
        db.MotionSpans.AddRange(Span(a.Id, now.AddHours(-30)), Span(b.Id, now.AddHours(-30)), Span(b.Id, now.AddHours(-2)), Span(hidden.Id, now.AddHours(-30)));
        await db.SaveChangesAsync();
        await new ActivityRollupService(db).RollupAsync(now);

        var all = await Service(db).GetHeatmapAsync(null, new HashSet<Guid> { a.Id, b.Id }, "7d", null, ["Motion"]);
        var everyone = await Service(db).GetHeatmapAsync(null, null, "7d", null, ["Motion"]);
        var forbidden = await Service(db).GetHeatmapAsync(hidden.Id, new HashSet<Guid> { a.Id }, "7d", null, null);

        Assert.Equal(3, all!.Total);
        Assert.Equal("All cameras", all.CameraName);
        Assert.Equal(4, everyone!.Total);
        Assert.Null(forbidden);
    }

    [Fact]
    public void ByDayViewHasAColumnPerLocalDateAndNamesTheBusiestDay()
    {
        var counts = new[]
        {
            At(new DateTime(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc), "Human", "person", 5),  // Fri Oct 2, 6 PM PDT
            At(new DateTime(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc), "Human", "person", 2), // Sat Oct 10, noon PDT
        };

        var map = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "30d", Now, counts, null, view: "calendar");

        Assert.Equal("calendar", map.Mode);
        Assert.Equal(24, map.RowLabels.Count);
        Assert.Equal("2026-10-13", map.ColumnLabels[^1]);
        Assert.Equal(31, map.ColumnLabels.Count); // the partial first day plus 30 more
        var fri = map.ColumnLabels.IndexOf("2026-10-02");
        Assert.Equal(5, map.Cells[18][fri]);
        Assert.Equal(5, map.DayTotals![fri]);
        Assert.Equal("Busiest day: Fri, Oct 2 (5 events).", map.BusiestDay);
    }

    [Fact]
    public void TypicalWeekCountsEachWeekdayForAverages()
    {
        var map = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "30d", Now,
            [At(new DateTime(2026, 10, 10, 19, 0, 0, DateTimeKind.Utc), "Human", null, 8)], null);

        Assert.Equal("week", map.Mode);
        Assert.Equal(31, map.WeekdayOccurrences!.Sum());
        Assert.All(map.WeekdayOccurrences!, n => Assert.InRange(n, 4, 5));
        Assert.Equal("Busiest day: Sat, Oct 10 (8 events).", map.BusiestDay);
    }

    [Fact]
    public void AChoiceThisCameraHasNoneOfFallsBackToTheDefault()
    {
        var hour = new DateTime(2026, 10, 12, 18, 0, 0, DateTimeKind.Utc);
        var counts = new[] { At(hour, "Vehicle", "car", 3), At(hour, ActivityHeatmapBuilder.MotionGroup, null, 9) };

        var fallback = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", Now, counts, ["Animal:cat"]);
        var partial = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", Now, counts, ["Animal:cat", "Motion"]);
        var none = ActivityHeatmapBuilder.Build(Guid.NewGuid(), "cam", Pacific, "7d", Now, counts, [ActivityHeatmapBuilder.NoneSelected]);

        Assert.Equal(["Vehicle"], fallback.Selection);
        Assert.Equal(3, fallback.Total);
        Assert.Equal(["Motion"], partial.Selection);
        Assert.Equal(0, none.Total);
    }
}
