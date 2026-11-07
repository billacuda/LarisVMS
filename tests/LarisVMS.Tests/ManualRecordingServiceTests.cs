using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Node;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class ManualRecordingServiceTests
{
    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Camera NewCamera(bool enabled = true) => new()
    {
        Id = Guid.NewGuid(), Name = "cam", Host = "10.0.0.1",
        DeviceServiceUri = "http://10.0.0.1/onvif/device_service", IsEnabled = enabled
    };

    private static CameraStream NewStream(Camera camera, DateTime? healthReportedAt, bool? keeping) => new()
    {
        Id = Guid.NewGuid(), CameraId = camera.Id, Role = CameraStreamRole.Main, RtspUri = "rtsp://x",
        HealthReportedAt = healthReportedAt, IsKeepingFootage = keeping
    };

    [Fact]
    public async Task StartingTwiceLeavesOneRecording()
    {
        using var db = NewDb();
        var camera = NewCamera();
        db.Add(camera);
        await db.SaveChangesAsync();
        var service = new ManualRecordingService(db);

        var first = await service.StartAsync([camera.Id], "alice");
        var second = await service.StartAsync([camera.Id], "bob");

        Assert.Equal([camera.Id], first.Changed);
        Assert.Empty(second.Changed);
        Assert.Equal([camera.Id], second.Unchanged);
        var row = Assert.Single(await db.ManualRecordings.ToListAsync());
        Assert.Equal("alice", row.StartedBy);
    }

    [Fact]
    public async Task StopClosesDuplicateOpenRowsAndRecordsWho()
    {
        using var db = NewDb();
        var camera = NewCamera();
        db.Add(camera);
        db.ManualRecordings.AddRange(
            new ManualRecording { CameraId = camera.Id, StartUtc = DateTime.UtcNow },
            new ManualRecording { CameraId = camera.Id, StartUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var result = await new ManualRecordingService(db).StopAsync([camera.Id], "alice");

        Assert.Equal([camera.Id], result.Changed);
        Assert.All(await db.ManualRecordings.ToListAsync(), r =>
        {
            Assert.NotNull(r.EndUtc);
            Assert.Equal("alice", r.EndedBy);
        });
    }

    [Fact]
    public async Task StoppingACameraThatIsntRecordingChangesNothing()
    {
        using var db = NewDb();
        var camera = NewCamera();
        db.Add(camera);
        await db.SaveChangesAsync();

        var result = await new ManualRecordingService(db).StopAsync([camera.Id], "alice");

        Assert.Empty(result.Changed);
        Assert.Equal([camera.Id], result.Unchanged);
    }

    [Fact]
    public async Task DisabledAndUnknownCamerasAreSkippedInABatch()
    {
        using var db = NewDb();
        var enabled = NewCamera();
        var disabled = NewCamera(enabled: false);
        var unknown = Guid.NewGuid();
        db.AddRange(enabled, disabled);
        await db.SaveChangesAsync();

        var result = await new ManualRecordingService(db).StartAsync([enabled.Id, disabled.Id, unknown], "alice");

        Assert.Equal([enabled.Id], result.Changed);
        Assert.Equal(2, result.Skipped.Count);
        Assert.Contains(disabled.Id, result.Skipped);
        Assert.Contains(unknown, result.Skipped);
    }

    [Fact]
    public async Task AFreshReportThatFootageIsBeingKeptReadsAsKeeping()
    {
        using var db = NewDb();
        var camera = NewCamera();
        db.AddRange(camera, NewStream(camera, DateTime.UtcNow, keeping: true));
        await db.SaveChangesAsync();

        var state = Assert.Single(await new ManualRecordingService(db).GetStatesAsync());

        Assert.True(state.Keeping);
        Assert.False(state.Manual);
    }

    [Fact]
    public async Task AStaleReportReadsAsNotKeeping()
    {
        using var db = NewDb();
        var camera = NewCamera();
        db.AddRange(camera, NewStream(camera, DateTime.UtcNow.AddMinutes(-5), keeping: true));
        await db.SaveChangesAsync();

        Assert.Empty(await new ManualRecordingService(db).GetStatesAsync());
    }

    [Fact]
    public async Task ManualOnAFreshCameraReadsAsKeepingBeforeTheNodeHasReportedIt()
    {
        using var db = NewDb();
        var camera = NewCamera();
        db.AddRange(camera, NewStream(camera, DateTime.UtcNow, keeping: false));
        db.ManualRecordings.Add(new ManualRecording { CameraId = camera.Id, StartUtc = DateTime.UtcNow, StartedBy = "alice" });
        await db.SaveChangesAsync();

        var state = Assert.Single(await new ManualRecordingService(db).GetStatesAsync());

        Assert.True(state.Keeping);
        Assert.True(state.Manual);
        Assert.Equal("alice", state.ManualStartedBy);
    }

    [Fact]
    public async Task ManualOnAnOfflineCameraIsManualButNotKeeping()
    {
        using var db = NewDb();
        var camera = NewCamera();
        db.AddRange(camera, NewStream(camera, DateTime.UtcNow.AddMinutes(-5), keeping: true));
        db.ManualRecordings.Add(new ManualRecording { CameraId = camera.Id, StartUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var state = Assert.Single(await new ManualRecordingService(db).GetStatesAsync());

        Assert.False(state.Keeping);
        Assert.True(state.Manual);
    }

    [Fact]
    public async Task NodesAreSentRunningAndRecentlyStoppedRecordingsButNotOldOnes()
    {
        using var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = "hash", MediaSigningKey = "key", StorageRootPath = @"E:\rec"
        };
        var running = NewCamera();
        var recent = NewCamera();
        var old = NewCamera();
        db.AddRange(node, running, recent, old);
        var now = DateTime.UtcNow;
        db.ManualRecordings.AddRange(
            new ManualRecording { CameraId = running.Id, StartUtc = now.AddMinutes(-1) },
            new ManualRecording { CameraId = recent.Id, StartUtc = now.AddMinutes(-30), EndUtc = now.AddMinutes(-10) },
            new ManualRecording { CameraId = old.Id, StartUtc = now.AddHours(-5), EndUtc = now.AddHours(-4) });
        await db.SaveChangesAsync();

        var ranges = (await new NodeService(db, new SettingsResolver(db)).GetConfigAsync(node.Id)).ManualRecordingRanges!;

        Assert.Equal(2, ranges.Count);
        Assert.Equal(ManualRecordingService.OpenEndUtc, ranges.Single(r => r.CameraId == running.Id).ToUtc);
        Assert.Contains(ranges, r => r.CameraId == recent.Id);
    }

    [Fact]
    public async Task RetentionRemovesStoppedRecordingsWithNoFootageButNeverRunningOnes()
    {
        using var db = NewDb();
        var camera = NewCamera();
        db.Add(camera);
        var now = DateTime.UtcNow;
        db.ManualRecordings.AddRange(
            new ManualRecording { CameraId = camera.Id, StartUtc = now.AddDays(-9), EndUtc = now.AddDays(-8) },
            new ManualRecording { CameraId = camera.Id, StartUtc = now.AddMinutes(-20), EndUtc = now.AddMinutes(-10) },
            new ManualRecording { CameraId = camera.Id, StartUtc = now.AddDays(-9) });
        await db.SaveChangesAsync();

        await MotionSpanRetentionService.SweepManualRecordingsAsync(db, CancellationToken.None);

        var rows = await db.ManualRecordings.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.EndUtc is null);
        Assert.Contains(rows, r => r.EndUtc > now.AddHours(-1));
    }

    [Fact]
    public void AnOpenRangeCoversSegmentsFromItsStartOn()
    {
        var cameraId = Guid.NewGuid();
        var start = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        var footage = new ProtectedFootage([new NodeConfigProtectedRangeDto(cameraId, start, ManualRecordingService.OpenEndUtc)]);

        Assert.True(footage.Covers(cameraId, start.AddSeconds(-30), start.AddSeconds(30)));
        Assert.True(footage.Covers(cameraId, start.AddDays(30), start.AddDays(30).AddMinutes(1)));
        Assert.False(footage.Covers(cameraId, start.AddMinutes(-2), start.AddMinutes(-1)));
        Assert.False(footage.Covers(Guid.NewGuid(), start, start.AddMinutes(1)));
    }

    [Theory]
    [InlineData(RecordingMode.Continuous, false, false, false, false, true)]
    [InlineData(RecordingMode.Manual, false, true, true, true, false)]    // nothing else counts, and no fail-open
    [InlineData(RecordingMode.Manual, true, false, false, false, true)]
    [InlineData(RecordingMode.Schedule, false, true, false, false, true)]
    [InlineData(RecordingMode.Schedule, false, false, true, true, false)]
    [InlineData(RecordingMode.Schedule, true, false, false, false, true)]
    [InlineData(RecordingMode.Motion, false, false, true, true, true)]    // during motion or post-roll
    [InlineData(RecordingMode.Motion, false, false, true, false, false)]
    [InlineData(RecordingMode.Motion, false, false, false, false, true)]  // nothing configured fails open
    [InlineData(RecordingMode.Motion, true, false, true, false, true)]
    [InlineData(RecordingMode.Event, false, false, true, false, false)]
    [InlineData(RecordingMode.Event, false, false, true, true, true)]
    public void KeepingStateFollowsTheCamerasMode(RecordingMode mode, bool manualOn, bool inSchedule, bool hasSource, bool hadSignal, bool expected)
    {
        Assert.Equal(expected, NodeWorker.IsKeeping(mode, manualOn, inSchedule, hasSource, hadSignal));
    }

    [Fact]
    public void ADiscardWaitsOutTheGracePeriod()
    {
        var end = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(NodeWorker.ShouldWaitBeforeDiscard(end, end.AddSeconds(5)));
        Assert.False(NodeWorker.ShouldWaitBeforeDiscard(end, end + NodeWorker.ManualStartGrace));
    }

    [Fact]
    public void TheGracePeriodOutlastsTheNodesConfigPoll()
    {
        Assert.True(NodeWorker.ManualStartGrace > TimeSpan.FromSeconds(30));
        Assert.True(ManualRecordingService.EndedRangeWindow > NodeWorker.ManualStartGrace + TimeSpan.FromMinutes(5));
    }
}
