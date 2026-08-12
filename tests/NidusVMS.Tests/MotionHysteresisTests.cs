using NidusVMS.Media;

namespace NidusVMS.Tests;

public class MotionHysteresisTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SingleFrameBlipNeverOpensASpan()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(1), endAfter: TimeSpan.FromSeconds(3));

        // One frame of motion, then immediately quiet — never held long enough to confirm a start.
        Assert.Null(h.Observe(T0, motionPresent: true, score: 0.9));
        Assert.Null(h.Observe(T0.AddSeconds(0.2), motionPresent: false, score: 0.0));
        Assert.Null(h.Flush(T0.AddSeconds(1)));
    }

    [Fact]
    public void SustainedMotionOpensThenClosesASpanAfterQuietPeriod()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(1), endAfter: TimeSpan.FromSeconds(3));

        Assert.Null(h.Observe(T0, true, 0.5));
        Assert.Null(h.Observe(T0.AddSeconds(0.5), true, 0.6));
        // Motion has now held for exactly startAfter — this is the confirming tick.
        Assert.Null(h.Observe(T0.AddSeconds(1.0), true, 0.7));
        Assert.Null(h.Observe(T0.AddSeconds(1.5), false, 0.0)); // quiet starts
        Assert.Null(h.Observe(T0.AddSeconds(3.0), false, 0.0)); // quiet for 1.5s — not enough yet

        var result = h.Observe(T0.AddSeconds(4.6), false, 0.0); // quiet since 1.5s -> now is 3.1s

        Assert.NotNull(result);
        // Span start is backdated to when motion actually began (T0), not the T0+1.0 confirmation.
        Assert.Equal(T0, result!.StartUtc);
        Assert.Equal(T0.AddSeconds(4.6), result.EndUtc);
        Assert.Equal(0.7, result.PeakScore);
    }

    [Fact]
    public void BriefQuietBlipDuringMotionDoesNotResetTheSpan()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(1), endAfter: TimeSpan.FromSeconds(3));

        h.Observe(T0, true, 0.5);
        h.Observe(T0.AddSeconds(1.0), true, 0.5); // confirmed open

        // One quiet frame that doesn't last endAfter, then motion resumes.
        h.Observe(T0.AddSeconds(1.5), false, 0.0);
        var midResult = h.Observe(T0.AddSeconds(2.0), true, 0.9); // motion resumes before endAfter elapses
        Assert.Null(midResult); // span still open, not closed by the blip

        // Now let it actually end.
        h.Observe(T0.AddSeconds(2.5), false, 0.0);
        var result = h.Observe(T0.AddSeconds(5.6), false, 0.0); // quiet since 2.5s -> 3.1s elapsed

        Assert.NotNull(result);
        Assert.Equal(T0, result!.StartUtc); // still backdated to the original start
        Assert.Equal(0.9, result.PeakScore); // peak from the resumed motion, not the first burst
    }

    [Fact]
    public void FlushClosesAnInProgressSpanWithoutWaitingOutEndAfter()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(1), endAfter: TimeSpan.FromSeconds(3));

        h.Observe(T0, true, 0.4);
        h.Observe(T0.AddSeconds(1.0), true, 0.4); // confirmed open, still "in motion" (no quiet tick yet)

        var result = h.Flush(T0.AddSeconds(1.2));

        Assert.NotNull(result);
        Assert.Equal(T0, result!.StartUtc);
        Assert.Equal(T0.AddSeconds(1.2), result.EndUtc);
    }

    [Fact]
    public void FlushWithNoOpenSpanReturnsNull()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(1), endAfter: TimeSpan.FromSeconds(3));

        Assert.Null(h.Flush(T0));
    }

    [Fact]
    public void IsActiveReflectsOnlyAConfirmedOpenSpan()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(1), endAfter: TimeSpan.FromSeconds(3));

        Assert.False(h.IsActive);
        h.Observe(T0, true, 0.5); // not yet confirmed — startAfter hasn't elapsed
        Assert.False(h.IsActive);
        h.Observe(T0.AddSeconds(1.0), true, 0.5); // confirmed
        Assert.True(h.IsActive);
        h.Observe(T0.AddSeconds(1.5), false, 0.0); // quiet started, but span not closed yet
        Assert.True(h.IsActive);
        h.Observe(T0.AddSeconds(4.6), false, 0.0); // endAfter elapsed — span closes
        Assert.False(h.IsActive);
    }

    private static readonly TimeSpan DefaultRecency = TimeSpan.FromSeconds(20);

    [Fact]
    public void CurrentInProgressSpanIsNullWhenNoActivityHasEverBeenObserved()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(1), endAfter: TimeSpan.FromSeconds(3));

        Assert.Null(h.CurrentInProgressSpan(T0, DefaultRecency));
    }

    [Fact]
    public void CurrentInProgressSpanSnapshotsAConfirmedOpenSpanWithoutClosingIt()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.FromSeconds(3));

        h.Observe(T0, true, 0.4); // confirmed immediately (startAfter is zero)
        h.Observe(T0.AddSeconds(2), true, 0.9); // peak rises

        var checkpoint1 = h.CurrentInProgressSpan(T0.AddSeconds(5), DefaultRecency);
        Assert.NotNull(checkpoint1);
        Assert.Equal(T0, checkpoint1!.StartUtc);
        Assert.Equal(T0.AddSeconds(5), checkpoint1.EndUtc);
        Assert.Equal(0.9, checkpoint1.PeakScore);

        // Snapshotting must not close the span — a later checkpoint still sees it open, and the
        // real close (via Observe/Flush) still works normally afterward.
        Assert.True(h.IsActive);
        var checkpoint2 = h.CurrentInProgressSpan(T0.AddSeconds(20), DefaultRecency);
        Assert.Equal(T0, checkpoint2!.StartUtc);
        Assert.Equal(T0.AddSeconds(20), checkpoint2.EndUtc);

        var closed = h.Flush(T0.AddSeconds(25));
        Assert.Equal(T0, closed!.StartUtc);
        Assert.Equal(T0.AddSeconds(25), closed.EndUtc);
    }

    [Fact]
    public void CurrentInProgressSpanReportsUnconfirmedActivityToo()
    {
        // startAfter is large enough that this run of motion never gets confirmed — this is the
        // exact real-world case the bug fix targets: frequent short bursts, each too brief to
        // individually confirm, that were previously invisible on the timeline despite already
        // driving the (raw-LastMotionAtUtc-based) segment-retention decision.
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(30), endAfter: TimeSpan.FromSeconds(3));

        h.Observe(T0, true, 0.6);
        Assert.False(h.IsActive); // confirmed this is genuinely unconfirmed

        var checkpoint = h.CurrentInProgressSpan(T0.AddSeconds(2), DefaultRecency);

        Assert.NotNull(checkpoint);
        Assert.Equal(T0, checkpoint!.StartUtc); // anchored to _motionSince, the only start available
        Assert.Equal(T0.AddSeconds(2), checkpoint.EndUtc);
    }

    [Fact]
    public void CurrentInProgressSpanGoesStaleOutsideTheRecencyWindowEvenIfTechnicallyStillOpen()
    {
        // endAfter deliberately larger than the recency window used below, so there's a real gap
        // between "_spanStart hasn't cleared yet" (IsActive still true) and "nothing happened
        // recently enough to report" — the recency check must govern reporting independently of
        // whether the span has formally closed.
        var h = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.FromSeconds(60));
        var recency = TimeSpan.FromSeconds(5);

        h.Observe(T0, true, 0.5); // confirmed immediately
        h.Observe(T0.AddSeconds(1), false, 0.0); // goes quiet — span stays open (endAfter is 60s)

        Assert.True(h.IsActive);
        Assert.Null(h.CurrentInProgressSpan(T0.AddSeconds(30), recency)); // 29s since last motion — stale
    }

    [Fact]
    public void LastMotionAtUtcUpdatesEvenForUnconfirmedMotion()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.FromSeconds(5), endAfter: TimeSpan.FromSeconds(3));

        Assert.Null(h.LastMotionAtUtc);
        // A single frame of motion never reaches startAfter=5s, so IsActive stays false throughout —
        // LastMotionAtUtc must still track it, since padding is meant to be conservative even for
        // motion too brief to become its own reported span.
        h.Observe(T0, true, 0.5);
        Assert.False(h.IsActive);
        Assert.Equal(T0, h.LastMotionAtUtc);
    }

    [Fact]
    public void LastMotionAtUtcIsMonotonicallyNonDecreasing()
    {
        // MotionSession.HasMotionSince (M8 pass 3) depends on this property to answer "did motion
        // happen anywhere in an interval" from a single comparison evaluated after the interval has
        // elapsed — if a later Observe call could ever move LastMotionAtUtc backward, that trick
        // would be unsound.
        var h = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.FromSeconds(3));

        h.Observe(T0, true, 0.5);
        Assert.Equal(T0, h.LastMotionAtUtc);

        h.Observe(T0.AddSeconds(1), false, 0.0); // quiet tick — must not reset or move it backward
        Assert.Equal(T0, h.LastMotionAtUtc);

        h.Observe(T0.AddSeconds(2), true, 0.5); // motion resumes — moves forward
        Assert.Equal(T0.AddSeconds(2), h.LastMotionAtUtc);
    }

    [Fact]
    public void CurrentInProgressSpanWithAnEffectivelyUnlimitedRecencyNeverGoesStale()
    {
        // What CameraEventSession.CurrentInProgressSpan (M8 pass 6) relies on: a camera-pushed
        // motion span can legitimately go a long stretch between ONVIF notifications while still
        // genuinely open — many real implementations send exactly one notification per edge, not a
        // periodic "still active" heartbeat — so it passes TimeSpan.MaxValue as recency specifically
        // to opt out of the staleness check CurrentInProgressSpanGoesStaleOutsideTheRecencyWindow...
        // above proves exists for a bounded recency.
        var h = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.FromSeconds(60));

        h.Observe(T0, true, 0.5); // confirmed immediately, span open, no further ticks after this

        Assert.True(h.IsActive);
        var checkpoint = h.CurrentInProgressSpan(T0.AddHours(2), TimeSpan.MaxValue);

        Assert.NotNull(checkpoint);
        Assert.Equal(T0, checkpoint!.StartUtc);
        Assert.Equal(T0.AddHours(2), checkpoint.EndUtc);
    }

    [Fact]
    public void LastMotionAtUtcGoesStaleDuringALongGapEvenThoughIsActiveStaysTrue()
    {
        // The exact gap M8 pass 6's recording-gating fix addresses. A camera that sends one
        // notification on the rising edge and nothing again until the falling edge leaves
        // LastMotionAtUtc frozen at the rising timestamp for the entire gap, even though the span is
        // still genuinely open — a recency-based check alone (LastMotionAtUtc >= some threshold)
        // would treat a long gap like this as "gone stale" and start discarding segments in the
        // middle of a still-ongoing event. IsActive has no timeout of its own — it only goes false
        // once an actual falling-edge tick closes the span — so NodeWorker.DecideMotionSegment ORs
        // it in alongside HasMotionSince/LastMotionAtUtc-based checks precisely so recording keeps
        // being retained until the camera actually says motion stopped, not until some arbitrary
        // staleness window expires.
        var h = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.FromSeconds(2));

        h.Observe(T0, true, 1.0); // rising edge — span opens immediately (startAfter is zero)
        var muchLater = T0.AddMinutes(10); // no further notifications for the whole gap

        Assert.Equal(T0, h.LastMotionAtUtc); // frozen — this is the stale signal on its own
        Assert.True(h.IsActive); // but still genuinely open — no falling edge has arrived yet

        // The falling edge finally arrives, closing the span for real (endAfter still applies to
        // the falling edge itself, same as any other close).
        Assert.Null(h.Observe(muchLater, false, 0.0));
        var closed = h.Observe(muchLater.AddSeconds(3), false, 0.0);

        Assert.NotNull(closed);
        Assert.Equal(T0, closed!.StartUtc);
        Assert.False(h.IsActive);
    }

    [Fact]
    public void PeakScoreTracksTheMaximumObservedDuringTheSpanNotTheLast()
    {
        var h = new MotionHysteresis(startAfter: TimeSpan.Zero, endAfter: TimeSpan.FromSeconds(3));

        h.Observe(T0, true, 0.9); // confirmed immediately (startAfter is zero)
        h.Observe(T0.AddSeconds(0.5), true, 0.3); // lower — must not overwrite the peak

        var result = h.Flush(T0.AddSeconds(1));

        Assert.NotNull(result);
        Assert.Equal(0.9, result!.PeakScore);
    }
}
