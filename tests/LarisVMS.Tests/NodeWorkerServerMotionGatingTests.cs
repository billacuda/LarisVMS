using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Covers NodeWorker.ShouldRunServerMotion — detection/hardware-acceleration overhaul,
/// pass 0. Camera.ServerMotionEnabled is a new toggle, added at the user's explicit request after
/// clarifying that MotionDetectionSource only decides which signal *gates Motion-mode recording*,
/// not whether ServerMotion runs at all: before this toggle existed, a camera whose chosen source was
/// something else (AI detection, an ONVIF event, a vendor integration) still paid for a continuous
/// software-or-GPU decode of its Sub stream purely to keep tagging the timeline with plain motion as
/// a fallback. Default true preserves that existing behavior on every camera; this toggle is how a
/// user opts a specific camera out to reclaim the CPU/decode cost instead.</summary>
public class NodeWorkerServerMotionGatingTests
{
    [Fact]
    public void RunsWhenEnabledWithZonesAndASubStream()
    {
        Assert.True(NodeWorker.ShouldRunServerMotion(serverMotionEnabled: true, serverMotionZoneCount: 1, hasSubStream: true));
    }

    [Fact]
    public void DoesNotRunWhenDisabledEvenWithZonesAndASubStream()
    {
        // The actual new behavior this pass adds: an explicit opt-out stops the session even though
        // every pre-existing condition (zones configured, Sub stream present) is satisfied.
        Assert.False(NodeWorker.ShouldRunServerMotion(serverMotionEnabled: false, serverMotionZoneCount: 1, hasSubStream: true));
    }

    [Fact]
    public void DoesNotRunWithNoZonesRegardlessOfTheToggle()
    {
        // Pre-existing behavior, unchanged: nothing to watch means nothing runs, toggle aside.
        Assert.False(NodeWorker.ShouldRunServerMotion(serverMotionEnabled: true, serverMotionZoneCount: 0, hasSubStream: true));
    }

    [Fact]
    public void DoesNotRunWithNoSubStreamRegardlessOfTheToggle()
    {
        Assert.False(NodeWorker.ShouldRunServerMotion(serverMotionEnabled: true, serverMotionZoneCount: 1, hasSubStream: false));
    }

    [Fact]
    public void DisabledWithNothingElseConfiguredStillDoesNotRun()
    {
        Assert.False(NodeWorker.ShouldRunServerMotion(serverMotionEnabled: false, serverMotionZoneCount: 0, hasSubStream: false));
    }
}
