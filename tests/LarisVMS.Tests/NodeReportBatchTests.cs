using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>What a node sends after it couldn't reach the server for a while, and how often it retries
/// an update that fails.</summary>
public class NodeReportBatchTests
{
    private static readonly DateTime T0 = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void StreamInfoIsMergedToOneReportPerStreamKeepingTheNewestKnownValueOfEachField()
    {
        var camera = Guid.NewGuid();
        var other = Guid.NewGuid();
        var merged = StreamInfoReportItem.Merge(
        [
            new(camera, "Main", 1920, 1080, "hevc"),
            new(camera, "Main", null, null, null, Fps: 15, BitrateKbps: 900),
            new(other, "Main", null, null, null, Fps: 25),
            new(camera, "Main", null, null, null, Fps: 14, AudioCodec: "aac"),
        ]);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new StreamInfoReportItem(camera, "Main", 1920, 1080, "hevc", Fps: 14, BitrateKbps: 900, AudioCodec: "aac"),
            merged.Single(m => m.CameraId == camera));
    }

    [Fact]
    public void RepeatedCheckpointsOfOneSpanBecomeItsLastOne()
    {
        var camera = Guid.NewGuid();
        var zone = Guid.NewGuid();
        var result = NodeWorker.DropSupersededCheckpoints(
        [
            new(camera, zone, T0, T0.AddSeconds(15), 0.2),
            new(camera, zone, T0, T0.AddSeconds(45), 0.9),
            new(camera, null, T0, T0.AddSeconds(15), 0.1),
            new(camera, zone, T0, T0.AddSeconds(30), 0.4),
            new(camera, zone, T0.AddMinutes(5), T0.AddMinutes(6), 0.3),
        ]);

        Assert.Equal(3, result.Count);
        var span = result.Single(r => r.ZoneId == zone && r.StartUtc == T0);
        Assert.Equal(T0.AddSeconds(45), span.EndUtc);
        Assert.Equal(0.9, span.Score);
    }

    [Fact]
    public void AiDetectionReportsAreAllKept()
    {
        var camera = Guid.NewGuid();
        var result = NodeWorker.DropSupersededCheckpoints(
        [
            new(camera, null, T0, T0.AddSeconds(15), 0.2, DetectedObjectLabel: "person"),
            new(camera, null, T0, T0.AddSeconds(30), 0.4, DetectedObjectLabel: "person"),
        ]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void AFailingUpdateIsRetriedLessAndLessOftenAndANewVersionAtOnce()
    {
        var backoff = new UpdateBackoff();
        Assert.True(backoff.ShouldTry("1.0", T0));
        Assert.False(backoff.ShouldTry("1.0", T0.AddSeconds(29)));
        Assert.True(backoff.ShouldTry("1.0", T0.AddSeconds(30)));
        Assert.False(backoff.ShouldTry("1.0", T0.AddSeconds(89)));
        Assert.True(backoff.ShouldTry("1.0", T0.AddSeconds(90)));
        Assert.True(backoff.ShouldTry("1.1", T0.AddSeconds(91)));
    }
}
