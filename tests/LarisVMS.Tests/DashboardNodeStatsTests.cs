using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>The dashboard's per-node card: host load sampling math, the per-window detection counts
/// and the query behind them.</summary>
public class DashboardNodeStatsTests
{
    [Fact]
    public void CpuPercentIsBusyTimeOverTotalTimeSinceTheLastSample()
    {
        // Kernel time includes idle time: 1000 kernel (of which 600 idle) + 1000 user = 2000 total, 1400 busy.
        var before = new HostStatsSampler.CpuTimes(Idle: 10_000, Kernel: 20_000, User: 5_000);
        var after = new HostStatsSampler.CpuTimes(Idle: 10_600, Kernel: 21_000, User: 6_000);

        Assert.Equal(70.0, HostStatsSampler.CpuPercentBetween(before, after));
        Assert.Null(HostStatsSampler.CpuPercentBetween(after, before)); // counters went backwards
        Assert.Null(HostStatsSampler.CpuPercentBetween(before, before)); // no time elapsed
    }

    [Fact]
    public void NetworkRateIsBytesPerSecondAndNullOnACounterReset()
    {
        Assert.Equal(1_000_000, HostStatsSampler.RatePerSecond(5_000_000, 35_000_000, 30));
        Assert.Null(HostStatsSampler.RatePerSecond(35_000_000, 1_000, 30)); // adapter reset
        Assert.Null(HostStatsSampler.RatePerSecond(0, 1_000, 0.1));         // interval too short to mean anything
    }

    [Fact]
    public void WindowsAccumulateShorterWindowsIntoLongerOnes()
    {
        var cam = Guid.NewGuid();
        List<CameraEventCountRow> rows =
        [
            new(cam, "Ai", "Human", Bucket: 0, Spans: 1, Objects: 2),
            new(cam, "Ai", "Human", Bucket: 1, Spans: 3, Objects: 3),
            new(cam, "Ai", "Vehicle", Bucket: 2, Spans: 4, Objects: 5),
            new(cam, "Tag", null, Bucket: 3, Spans: 7, Objects: 7),
        ];

        Assert.Equal(new WindowCounts(1, 4, 8, 15), DashboardService.Windows(rows, _ => true, objects: false));
        Assert.Equal(new WindowCounts(2, 5, 5, 5), DashboardService.Windows(rows, r => r.Name == "Human", objects: true));
        // A custom tag is an event but never an object of any class: its 7 is left out.
        Assert.Equal(new WindowCounts(2, 5, 10, 10), DashboardService.Windows(rows, _ => true, objects: true));
    }

    [Fact]
    public async Task EventCountsGroupByKindAndAgeAndSkipPlainMotion()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "c", Host = "h", DeviceServiceUri = "http://h/onvif/device_service" };
        var human = new DetectedObjectCategory { Id = Guid.NewGuid(), Name = "Human", ColorHex = "#fff" };
        db.AddRange(camera, human);

        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        MotionSpan Span(TimeSpan ago, Action<MotionSpan> set)
        {
            var m = new MotionSpan { CameraId = camera.Id, StartUtc = now - ago, EndUtc = now - ago + TimeSpan.FromSeconds(10) };
            set(m);
            return m;
        }
        db.MotionSpans.AddRange(
            Span(TimeSpan.FromMinutes(10), m => { m.Source = MotionSource.AiDetection; m.DetectedObjectCategoryId = human.Id; m.DetectedObjectLabel = "person"; m.MovingCount = 2; }),
            Span(TimeSpan.FromHours(5), m => { m.Source = MotionSource.CameraEvent; m.DetectionKind = DetectionKind.Vehicle; }),
            Span(TimeSpan.FromDays(3), m => { m.Source = MotionSource.ServerMotion; }),                       // plain motion
            Span(TimeSpan.FromDays(40), m => { m.Source = MotionSource.CameraEvent; m.DetectionKind = DetectionKind.Human; })); // too old
        await db.SaveChangesAsync();

        var service = new TimelineService(db, new StubEventColors());
        var rows = await service.GetEventCountsAsync(null, now);

        Assert.Equal(2, rows.Count);
        var ai = Assert.Single(rows, r => r.Kind == "Ai");
        Assert.Equal(("Human", 0, 1, 2), (ai.Name, ai.Bucket, ai.Spans, ai.Objects));
        var cam = Assert.Single(rows, r => r.Kind == "Camera");
        Assert.Equal(("Vehicle", 1, 1, 1), (cam.Name, cam.Bucket, cam.Spans, cam.Objects));
    }

    private sealed class StubEventColors : IEventColorService
    {
        public Task<EventPalette> GetAsync(CancellationToken ct = default) => Task.FromResult(EventPalette.Empty);
        public Task SaveAsync(EventPalette p, string? modifiedBy, CancellationToken ct = default) => Task.CompletedTask;
    }
}
