using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>The dashboard's configurable widgets: ranking rules, the report filter, widget request
/// parsing, and the two queries added for them.</summary>
public class DashboardWidgetTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid NodeA = Guid.NewGuid();
    private static readonly Guid NodeB = Guid.NewGuid();

    private static DashCamera Camera(string name, Guid? node = null, bool enabled = true, DateTime? reported = null,
        int? fps = null, long storage = 0, long? quota = null, DateTime? latest = null, string? mode = null, bool aiOn = false,
        DateTime? created = null) =>
        new(Guid.NewGuid(), name, enabled, node, node is null ? null : "node", node,
            fps, null, null, reported, null, null, null, null, aiOn, created ?? Now.AddDays(-100),
            StorageBytes: storage, QuotaBytes: quota, LatestRecordingUtc: latest, RecordingMode: mode);

    private static DashNode Node(Guid id, string name, DateTime? lastSeen = null, double? cpu = null, double? days = null,
        bool pressure = false, DateTime? pressureSince = null, double? skew = null, string? version = null, string? latestBuild = null,
        NodeFailoverState failover = NodeFailoverState.Normal) =>
        new(id, name, lastSeen ?? Now, cpu, null, null, cpu is null ? null : Now, 100, 1000, days,
            pressure, pressureSince, skew, version, latestBuild, failover, null, null);

    private static DashboardWidgetData Data(IReadOnlyList<DashCamera> cameras, IReadOnlyList<DashNode>? nodes = null,
        IReadOnlyList<CameraEventCountRow>? events = null, IReadOnlyList<CameraEventCountRow>? motion = null) =>
        new(Now, cameras, nodes ?? [], events ?? [], motion ?? [], CanOpenNodes: true);

    [Fact]
    public void OfflineCamerasAreLongestOfflineFirstAndSkipHealthyDisabledAndUnassigned()
    {
        var d = Data([
            Camera("healthy", NodeA, reported: Now.AddSeconds(-10)),
            Camera("recent", NodeA, reported: Now.AddMinutes(-5)),
            Camera("old", NodeA, reported: Now.AddDays(-2)),
            Camera("never", NodeA, reported: null, created: Now.AddHours(-1)),
            Camera("disabled", NodeA, enabled: false, reported: Now.AddDays(-9)),
            Camera("unassigned", node: null, reported: Now.AddDays(-9)),
        ]);

        var rows = DashboardWidgetRanker.Rank(DashboardWidgetCatalog.CameraOffline, d);

        Assert.Equal(["old", "never", "recent"], rows.Select(r => r.AssetName));
        Assert.Equal("2d 0h", rows[0].Display);
        Assert.Equal("never reported", rows[1].Note);
    }

    [Fact]
    public void NoOfflineCamerasMeansNoRows()
    {
        var d = Data([Camera("healthy", NodeA, reported: Now.AddSeconds(-10))]);
        Assert.Empty(DashboardWidgetRanker.Rank(DashboardWidgetCatalog.CameraOffline, d));
        Assert.Equal("All cameras online", DashboardWidgetCatalog.Find(DashboardWidgetCatalog.CameraOffline)!.EmptyMessage);
    }

    [Fact]
    public void OnlyAContinuousCameraPastTheGapIsFlaggedAsShouldBeRecording()
    {
        var d = Data([
            Camera("continuous-stale", NodeA, latest: Now.AddMinutes(-10), mode: "Continuous"),
            Camera("continuous-fresh", NodeA, latest: Now.AddMinutes(-2), mode: "Continuous"),
            Camera("motion-stale", NodeA, latest: Now.AddHours(-3), mode: "Motion"),
        ]);

        var rows = DashboardWidgetRanker.Rank(DashboardWidgetCatalog.CameraLastRecording, d);

        Assert.Equal(["motion-stale", "continuous-stale", "continuous-fresh"], rows.Select(r => r.AssetName));
        Assert.Null(rows[0].Badge);
        Assert.Equal("Should be recording", rows[1].Badge);
        Assert.Null(rows[2].Badge);
    }

    [Fact]
    public void QuotaWidgetOnlyListsCamerasWithAQuota()
    {
        const long Gb = 1L << 30;
        var d = Data([
            Camera("no-quota", NodeA, storage: 900 * Gb),
            Camera("half", NodeA, storage: 50 * Gb, quota: 100 * Gb),
            Camera("full", NodeA, storage: 100 * Gb, quota: 100 * Gb),
            Camera("nearly", NodeA, storage: 95 * Gb, quota: 100 * Gb),
        ]);

        var rows = DashboardWidgetRanker.Rank(DashboardWidgetCatalog.CameraQuota, d);

        Assert.Equal(["full", "nearly", "half"], rows.Select(r => r.AssetName));
        Assert.Equal(["Full", "Nearly full", null], rows.Select(r => r.Badge));
    }

    [Fact]
    public void FrameRateCanBeRankedLowestFirst()
    {
        var d = Data([
            Camera("a", NodeA, reported: Now, fps: 30),
            Camera("b", NodeA, reported: Now, fps: 5),
            Camera("stale", NodeA, reported: Now.AddHours(-1), fps: 1),
        ]);

        Assert.Equal(["a", "b"], DashboardWidgetRanker.Rank(DashboardWidgetCatalog.CameraFps, d).Select(r => r.AssetName));
        Assert.Equal(["b", "a"], DashboardWidgetRanker.Rank(DashboardWidgetCatalog.CameraFps, d, new DashboardWidgetOptions(Ascending: true)).Select(r => r.AssetName));
    }

    [Fact]
    public void NodesWithoutCamerasAreLeftOutOfCameraDerivedWidgetsButNotLoadWidgets()
    {
        var d = Data(
            [Camera("cam", NodeA, reported: Now)],
            [Node(NodeA, "busy", cpu: 20, days: 5), Node(NodeB, "empty", cpu: 80, days: 1)]);

        Assert.Equal(["busy"], DashboardWidgetRanker.Rank(DashboardWidgetCatalog.NodeCameras, d).Select(r => r.AssetName));
        Assert.Equal(["busy"], DashboardWidgetRanker.Rank(DashboardWidgetCatalog.NodeDaysRemaining, d).Select(r => r.AssetName));
        Assert.Equal(["empty", "busy"], DashboardWidgetRanker.Rank(DashboardWidgetCatalog.NodeCpu, d).Select(r => r.AssetName));
        Assert.Equal(["busy", "empty"], DashboardWidgetRanker.Rank(DashboardWidgetCatalog.NodeStorage, d).Select(r => r.AssetName).OrderBy(n => n));
    }

    [Fact]
    public void NodeHealthIssuesAreWorstFirst()
    {
        var d = Data([], [
            Node(Guid.NewGuid(), "outdated", version: "0.200.0", latestBuild: "0.216.0"),
            Node(Guid.NewGuid(), "skewed", skew: 300),
            Node(Guid.NewGuid(), "pressured", pressure: true, pressureSince: Now.AddHours(-2)),
            Node(Guid.NewGuid(), "offline", lastSeen: Now.AddHours(-1)),
            Node(Guid.NewGuid(), "fine", skew: 2, version: "0.216.0", latestBuild: "0.216.0"),
        ]);

        var rows = DashboardWidgetRanker.Rank(DashboardWidgetCatalog.NodeHealth, d);

        Assert.Equal(["offline", "pressured", "skewed", "outdated"], rows.Select(r => r.AssetName));
        Assert.Equal(["Offline", "Storage pressure", "Clock skew", "Outdated"], rows.Select(r => r.Badge));
    }

    [Fact]
    public void EventWidgetsCountOneSourcePerCameraForTheChosenWindow()
    {
        var ai = Camera("ai", NodeA, aiOn: true);
        var native = Camera("native", NodeA, aiOn: false);
        List<CameraEventCountRow> events =
        [
            new(ai.Id, "Ai", "Human", Bucket: 0, Spans: 1, Objects: 3),
            new(ai.Id, "Camera", nameof(DetectionKind.Human), Bucket: 0, Spans: 9, Objects: 9), // ignored: camera runs AI
            new(native.Id, "Camera", nameof(DetectionKind.Human), Bucket: 2, Spans: 4, Objects: 4),
        ];
        var d = Data([ai, native], events: events);

        var day = DashboardWidgetRanker.Rank(DashboardWidgetCatalog.CameraHuman, d);
        Assert.Equal(["ai"], day.Select(r => r.AssetName));
        Assert.Equal(3, day[0].Value);

        var week = DashboardWidgetRanker.Rank(DashboardWidgetCatalog.CameraHuman, d, new DashboardWidgetOptions("7d"));
        Assert.Equal(["native", "ai"], week.Select(r => r.AssetName));
    }

    [Fact]
    public void ReportFilterMatchesNameNodeOrNoteAndCanResort()
    {
        List<DashboardWidgetRow> rows =
        [
            new("1", "Front door", "camera", null, NodeA, "Alpha", 3, "3"),
            new("2", "Back yard", "camera", null, NodeB, "Bravo", 9, "9", Note: "garden"),
            new("3", "Garage", "camera", null, NodeA, "Alpha", 1, "1"),
        ];

        Assert.Equal(["Back yard", "Garage"], DashboardWidgetService.FilterAndSort(rows, "gar", null, null, false).Select(r => r.AssetName));
        Assert.Equal(["Front door", "Garage"], DashboardWidgetService.FilterAndSort(rows, null, NodeA, null, false).Select(r => r.AssetName));
        Assert.Equal(["Back yard", "Front door", "Garage"], DashboardWidgetService.FilterAndSort(rows, null, null, "value", true).Select(r => r.AssetName));
        Assert.Equal(["Back yard", "Front door", "Garage"], DashboardWidgetService.FilterAndSort(rows, null, null, "name", false).Select(r => r.AssetName));
        // No sort column keeps the widget's own ranking order.
        Assert.Equal(["Front door", "Back yard", "Garage"], DashboardWidgetService.FilterAndSort(rows, null, null, null, false).Select(r => r.AssetName));
    }

    [Fact]
    public void WidgetSpecsParseWindowAndDirection()
    {
        Assert.Equal(("cam-fps", new DashboardWidgetOptions(null, true)), DashboardWidgetCatalog.ParseSpec("cam-fps|asc"));
        Assert.Equal(("cam-human", new DashboardWidgetOptions("7d", false)), DashboardWidgetCatalog.ParseSpec("cam-human|7d|bogus"));
        Assert.Equal(("cam-storage", new DashboardWidgetOptions()), DashboardWidgetCatalog.ParseSpec("cam-storage"));

        var (_, withKinds) = DashboardWidgetCatalog.ParseSpec("latest-detections|k=Human, Motion,human");
        Assert.Equal(["Human", "Motion"], withKinds.Kinds!);
        // "k=" alone is an explicit empty choice (everything unticked), not "use the default".
        Assert.Empty(DashboardWidgetCatalog.ParseSpec("latest-detections|k=").Options.Kinds!);
    }

    [Fact]
    public async Task SnapshotsOnlyIncludePlainMotionWhenAskedTo()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "c", Host = "h", DeviceServiceUri = "http://h/onvif/device_service" };
        db.Add(camera);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = camera.Id, StartUtc = Now.AddMinutes(-5), EndUtc = Now.AddMinutes(-4), Source = MotionSource.ServerMotion },
            new MotionSpan { CameraId = camera.Id, StartUtc = Now.AddMinutes(-3), EndUtc = Now.AddMinutes(-2), Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human });
        db.Segments.Add(new Segment { CameraId = camera.Id, NodeId = NodeA, FilePath = "a", StartUtc = Now.AddMinutes(-10), EndUtc = Now });
        await db.SaveChangesAsync();
        var service = new TimelineService(db, new StubEventColors());

        async Task<string[]> Labels(IReadOnlyCollection<string>? kinds, bool motion) =>
            (await service.GetSnapshotsAsync(null, null, null, 1, 10, default, kinds, null, motion)).Items.Select(i => i.Label).ToArray();

        Assert.Equal(["Human"], await Labels(null, motion: false));
        Assert.Equal(["Human", "Motion"], await Labels(null, motion: true));
        Assert.Equal(["Motion"], await Labels(["Motion"], motion: true));
        Assert.Equal(["Human"], await Labels(["Human"], motion: false));
    }

    [Fact]
    public void CatalogKeysAreUniqueAndTheDefaultLayoutOnlyUsesKnownWidgets()
    {
        Assert.Equal(DashboardWidgetCatalog.All.Count, DashboardWidgetCatalog.All.Select(w => w.Key).Distinct().Count());
        Assert.All(DashboardWidgetCatalog.DefaultLayout, i => Assert.NotNull(DashboardWidgetCatalog.Find(i.Key)));
    }

    [Fact]
    public async Task PlainMotionCountsAndRecordingExtentsComeFromTheirOwnRows()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var camera = new Camera { Id = Guid.NewGuid(), Name = "c", Host = "h", DeviceServiceUri = "http://h/onvif/device_service" };
        var empty = new Camera { Id = Guid.NewGuid(), Name = "e", Host = "h", DeviceServiceUri = "http://h/onvif/device_service" };
        db.AddRange(camera, empty);
        db.MotionSpans.AddRange(
            new MotionSpan { CameraId = camera.Id, StartUtc = Now.AddMinutes(-5), EndUtc = Now, Source = MotionSource.ServerMotion },
            new MotionSpan { CameraId = camera.Id, StartUtc = Now.AddHours(-5), EndUtc = Now, Source = MotionSource.ServerMotion },
            new MotionSpan { CameraId = camera.Id, StartUtc = Now.AddMinutes(-5), EndUtc = Now, Source = MotionSource.CameraEvent, DetectionKind = DetectionKind.Human });
        db.Segments.AddRange(
            new Segment { CameraId = camera.Id, NodeId = NodeA, FilePath = "a", StartUtc = Now.AddDays(-3), EndUtc = Now.AddDays(-3).AddMinutes(1) },
            new Segment { CameraId = camera.Id, NodeId = NodeA, FilePath = "b", StartUtc = Now.AddMinutes(-2), EndUtc = Now.AddMinutes(-1) });
        await db.SaveChangesAsync();

        var service = new TimelineService(db, new StubEventColors());

        var motion = await service.GetPlainMotionCountsAsync(null, Now);
        Assert.Equal([(0, 1), (1, 1)], motion.OrderBy(r => r.Bucket).Select(r => (r.Bucket, r.Spans)));
        Assert.All(motion, r => Assert.Equal("Motion", r.Kind));

        var extents = await service.GetRecordingExtentsAsync();
        Assert.Equal((Now.AddDays(-3), Now.AddMinutes(-1)), extents[camera.Id]);
        Assert.False(extents.ContainsKey(empty.Id));
    }

    private sealed class StubEventColors : IEventColorService
    {
        public Task<EventPalette> GetAsync(CancellationToken ct = default) => Task.FromResult(EventPalette.Empty);
        public Task SaveAsync(EventPalette p, string? modifiedBy, CancellationToken ct = default) => Task.CompletedTask;
    }
}
