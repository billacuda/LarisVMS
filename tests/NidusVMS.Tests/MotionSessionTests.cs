using Microsoft.Extensions.Logging.Abstractions;
using NidusVMS.Media;
using NidusVMS.Node;

namespace NidusVMS.Tests;

/// <summary>Covers MotionSession.HasMotionSince — the M8 pass 3 query NodeWorker's deferred
/// segment decision uses to answer "did motion happen anywhere in this interval," evaluated once
/// after the interval has elapsed. Drives each zone's hysteresis directly via the internal test
/// seam rather than spawning ffmpeg through RunAsync.</summary>
public class MotionSessionTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private static MotionSession NewSession(params Guid[] zoneIds)
    {
        var zones = zoneIds.Select(id => new MotionZoneMask(id, new bool[1], Sensitivity: 0.15)).ToList();
        return new MotionSession(new MotionSessionOptions("ffmpeg", "rtsp://unused"), zones, NullLogger.Instance);
    }

    [Fact]
    public void FreshSessionWithNoObservedFramesHasNoMotionSinceAnything()
    {
        var zoneId = Guid.NewGuid();
        var session = NewSession(zoneId);

        Assert.False(session.HasMotionSince(T0.AddYears(-1)));
    }

    [Fact]
    public void MotionOnAnyOneZoneCountsForTheWholeSession()
    {
        var zoneA = Guid.NewGuid();
        var zoneB = Guid.NewGuid();
        var session = NewSession(zoneA, zoneB);

        // Only zoneB ever sees motion — the session-wide query must still find it, since a segment
        // should be kept if *any* watched area had activity.
        session.TryGetZoneHysteresis(zoneB)!.Observe(T0, true, 0.9);

        Assert.True(session.HasMotionSince(T0.AddSeconds(-1)));
    }

    [Fact]
    public void HasMotionSinceIsFalseForAThresholdAfterTheLastObservedMotion()
    {
        var zoneId = Guid.NewGuid();
        var session = NewSession(zoneId);
        session.TryGetZoneHysteresis(zoneId)!.Observe(T0, true, 0.9);

        // Motion happened at T0 — a threshold at or before T0 finds it, a threshold after does not.
        Assert.True(session.HasMotionSince(T0));
        Assert.True(session.HasMotionSince(T0.AddSeconds(-5)));
        Assert.False(session.HasMotionSince(T0.AddSeconds(5)));
    }

    [Fact]
    public void UnknownZoneIdReturnsNullFromTheTestSeam()
    {
        var session = NewSession(Guid.NewGuid());

        Assert.Null(session.TryGetZoneHysteresis(Guid.NewGuid()));
    }

    private static readonly TimeSpan DefaultRecency = TimeSpan.FromSeconds(20);

    [Fact]
    public void GetInProgressSpansReturnsOnlyZonesWithRecentActivity()
    {
        var zoneA = Guid.NewGuid(); // will be active
        var zoneB = Guid.NewGuid(); // never triggers
        var session = NewSession(zoneA, zoneB);

        session.TryGetZoneHysteresis(zoneA)!.Observe(T0, true, 0.7);
        session.TryGetZoneHysteresis(zoneA)!.Observe(T0.AddSeconds(1), true, 0.7);

        var inProgress = session.GetInProgressSpans(T0.AddSeconds(10), DefaultRecency).ToList();

        Assert.Single(inProgress);
        Assert.Equal(zoneA, inProgress[0].ZoneId);
        Assert.Equal(T0, inProgress[0].Span.StartUtc);
        Assert.Equal(T0.AddSeconds(10), inProgress[0].Span.EndUtc);
    }

    [Fact]
    public void GetInProgressSpansIncludesUnconfirmedBurstsToo()
    {
        // The actual bug fix this pass targets: motion present but never held long enough to
        // confirm (startAfter is 1s by default and this run is shorter) still needs to show up in
        // a checkpoint, since retention (HasMotionSince) already treats it as real activity.
        var zoneId = Guid.NewGuid();
        var session = NewSession(zoneId);

        session.TryGetZoneHysteresis(zoneId)!.Observe(T0, true, 0.5); // single tick, never confirmed
        Assert.False(session.TryGetZoneHysteresis(zoneId)!.IsActive);

        var inProgress = session.GetInProgressSpans(T0.AddSeconds(2), DefaultRecency).ToList();

        Assert.Single(inProgress);
        Assert.Equal(zoneId, inProgress[0].ZoneId);
    }

    [Fact]
    public void GetInProgressSpansIsEmptyWhenNothingIsActive()
    {
        var session = NewSession(Guid.NewGuid(), Guid.NewGuid());

        Assert.Empty(session.GetInProgressSpans(T0, DefaultRecency));
    }

    // ── End-to-end pre-roll/post-roll scenario, mirroring exactly how NodeWorker uses these two
    // primitives together (HasMotionSince + ShouldDiscardSegment), not just each in isolation. This
    // is the actual case the pre-roll feature exists for — a segment that finishes recording
    // *before* motion starts must still survive once that motion arrives within the pre-roll
    // window, which requires deferring the decision rather than making it the instant the segment
    // completes. No NodeWorker/DI/filesystem involved — pure math against real DateTime values.

    [Fact]
    public void SegmentThatCompletesBeforeMotionStartsIsKeptOnceMotionArrivesWithinPreRoll()
    {
        var zoneId = Guid.NewGuid();
        var session = NewSession(zoneId);
        var preRoll = TimeSpan.FromSeconds(10);
        var postRoll = TimeSpan.FromSeconds(30);

        var segmentEndUtc = T0; // segment N completes here — no motion has happened yet
        // NodeWorker defers the actual decision to segmentEndUtc + preRoll (see
        // PendingMotionSegmentDecision.DecideAtUtc) — this test skips straight to what's checked at
        // that later moment, since the deferral itself is timing plumbing, not decision logic.

        // Motion starts 5s after the segment ended — inside the 10s pre-roll window.
        session.TryGetZoneHysteresis(zoneId)!.Observe(segmentEndUtc.AddSeconds(5), true, 0.9);

        var hadMotionInWindow = session.HasMotionSince(segmentEndUtc - postRoll);
        var shouldDiscard = NodeWorker.ShouldDiscardSegment("Motion", hasMotionSession: true, hadMotionInWindow);

        Assert.True(hadMotionInWindow);
        Assert.False(shouldDiscard); // kept — this segment is the pre-roll for the event that follows
    }

    [Fact]
    public void SegmentIsDiscardedWhenNoMotionArrivesWithinPreRollEither()
    {
        var zoneId = Guid.NewGuid();
        var session = NewSession(zoneId);
        var postRoll = TimeSpan.FromSeconds(30);
        var segmentEndUtc = T0;

        // No Observe calls at all — genuinely quiet camera. Whatever preRoll is set to, waiting
        // longer doesn't manufacture motion that never happened.

        var hadMotionInWindow = session.HasMotionSince(segmentEndUtc - postRoll);
        var shouldDiscard = NodeWorker.ShouldDiscardSegment("Motion", hasMotionSession: true, hadMotionInWindow);

        Assert.False(hadMotionInWindow);
        Assert.True(shouldDiscard);
    }

    [Fact]
    public void SegmentIsKeptAsPostRollOfAnEventThatEndedBeforeItCompleted()
    {
        var zoneId = Guid.NewGuid();
        var session = NewSession(zoneId);
        var postRoll = TimeSpan.FromSeconds(30);
        var segmentEndUtc = T0;

        // Motion happened 20s before this segment ended — inside the 30s post-roll window.
        session.TryGetZoneHysteresis(zoneId)!.Observe(segmentEndUtc.AddSeconds(-20), true, 0.9);

        var hadMotionInWindow = session.HasMotionSince(segmentEndUtc - postRoll);

        Assert.True(hadMotionInWindow);
        Assert.False(NodeWorker.ShouldDiscardSegment("Motion", hasMotionSession: true, hadMotionInWindow));
    }

    [Fact]
    public void SegmentWithMotionInItsOwnMiddleIsKeptEvenWithAShortPostRoll()
    {
        // The real bug this test locks in: NodeWorker used to anchor the lookback threshold to the
        // segment's *end* (segmentEndUtc - postRoll), which only reliably caught motion that happened
        // within postRoll seconds of the segment finishing. A 60s segment with real motion 40s before
        // it ended, and nothing since, was silently discarded once postRoll was set below 40s —
        // invisible with the original 30s default (close to half the segment) but a real, confirmed
        // data-loss bug once an operator configured a smaller value (e.g. 3s). The fix anchors to
        // segmentStartUtc instead, so anywhere in the segment counts, not just near its tail.
        var zoneId = Guid.NewGuid();
        var session = NewSession(zoneId);
        var postRoll = TimeSpan.FromSeconds(3);
        var segmentStartUtc = T0;
        var segmentEndUtc = T0.AddSeconds(60);

        // Motion 20s into a 60s segment — 40s before the segment ends, well outside a 3s post-roll
        // window measured from the end, but still squarely inside the segment itself.
        session.TryGetZoneHysteresis(zoneId)!.Observe(segmentStartUtc.AddSeconds(20), true, 0.9);

        var wronglyAnchoredToEnd = session.HasMotionSince(segmentEndUtc - postRoll);
        var correctlyAnchoredToStart = session.HasMotionSince(segmentStartUtc - postRoll);

        Assert.False(wronglyAnchoredToEnd); // demonstrates the bug: the old anchor misses it
        Assert.True(correctlyAnchoredToStart); // the fix: the new anchor catches it
        Assert.False(NodeWorker.ShouldDiscardSegment("Motion", hasMotionSession: true, correctlyAnchoredToStart));
    }
}
