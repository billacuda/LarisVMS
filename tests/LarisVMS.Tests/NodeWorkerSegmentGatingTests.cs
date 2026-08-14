using LarisVMS.Core.Dtos;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Covers NodeWorker.ShouldDiscardSegment — the M8 rule deciding whether a segment is
/// discarded (Motion/Event mode, no activity in its pre/post-roll window) or kept/reported, called
/// once per segment after its keep/discard decision has been deferred out to DecideAtUtc (see
/// PendingGatedSegmentDecision). This is the one piece of the recording-mode gating that's actually
/// safe to unit test in isolation; the surrounding deferral timing, file-deletion, and
/// MotionSession-lookup plumbing needs a real camera to verify. Mode-branching itself (which modes
/// even reach this function, what counts as a "signal source" per mode) lives in
/// NodeWorker.HandleSegmentCompleted/DecideGatedSegment — not unit-testable in isolation since it's
/// stateful, but ShouldDiscardSegment no longer takes a mode parameter at all: by the time a caller
/// reaches it, the mode is already known to be Motion or Event.
///
/// The bias throughout: default to KEEPING a segment whenever there's any doubt (no signal session
/// available). A false positive here silently deletes real footage, which is categorically worse
/// than a false negative (a camera that behaves like Continuous and just doesn't save the storage
/// it could have).</summary>
public class NodeWorkerSegmentGatingTests
{
    [Fact]
    public void NoSignalSessionNeverDiscardsRegardlessOfWindowState()
    {
        // Misconfigured camera (Motion/Event mode, no zone/rule/session) — must fail safe to "keep
        // everything," not "delete everything."
        Assert.False(NodeWorker.ShouldDiscardSegment(hasSignalSession: false, hadSignalInWindow: false));
        Assert.False(NodeWorker.ShouldDiscardSegment(hasSignalSession: false, hadSignalInWindow: true));
    }

    [Fact]
    public void SignalSessionKeepsSegmentsWithinTheWindow()
    {
        Assert.False(NodeWorker.ShouldDiscardSegment(hasSignalSession: true, hadSignalInWindow: true));
    }

    [Fact]
    public void SignalSessionDiscardsSegmentsOutsideTheWindow()
    {
        Assert.True(NodeWorker.ShouldDiscardSegment(hasSignalSession: true, hadSignalInWindow: false));
    }
}

/// <summary>Covers NodeWorker.IsWithinSchedule — the M8 Schedule-mode rule deciding whether a
/// segment's start time falls inside any of a camera's configured windows. Pure and unit-tested
/// directly against DateTime/TimeOnly math, no real clock or filesystem involved.
///
/// Every input below is constructed as DateTimeKind.Local, not DateTimeKind.Utc — IsWithinSchedule
/// calls .ToLocalTime() on its input, which is a no-op for an already-Local value but would
/// otherwise shift a Utc value by this machine's real UTC offset (confirmed live: this dev box runs
/// Pacific time, seven-plus hours off, which would have silently broken every hand-picked
/// wall-clock assertion below had the inputs been constructed as Utc). Using Local makes every
/// assertion describe the exact wall-clock time it claims to, regardless of what timezone whichever
/// machine actually runs this test suite happens to be in.</summary>
public class NodeWorkerIsWithinScheduleTests
{
    private static NodeConfigScheduleWindowDto Window(string days, string start, string end) =>
        new(Guid.NewGuid(), days, TimeOnly.Parse(start), TimeOnly.Parse(end));

    private static DateTime Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Local);

    [Fact]
    public void NoWindowsConfiguredFailsOpen()
    {
        Assert.True(NodeWorker.IsWithinSchedule([], DateTime.Now));
    }

    [Fact]
    public void SameDayWindowMatchesInsideItsRange()
    {
        var windows = new[] { Window("Monday", "09:00", "17:00") };
        Assert.True(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 17, 13, 0))); // Monday 1pm
    }

    [Fact]
    public void SameDayWindowDoesNotMatchOutsideItsRange()
    {
        var windows = new[] { Window("Monday", "09:00", "17:00") };
        Assert.False(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 17, 20, 0))); // Monday 8pm
    }

    [Fact]
    public void SameDayWindowDoesNotMatchOnADifferentDay()
    {
        var windows = new[] { Window("Monday", "09:00", "17:00") };
        Assert.False(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 18, 13, 0))); // Tuesday 1pm
    }

    [Fact]
    public void MidnightCrossingWindowMatchesLateOnItsStartingDay()
    {
        var windows = new[] { Window("Friday", "22:00", "02:00") };
        Assert.True(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 21, 23, 30))); // Friday 11:30pm
    }

    [Fact]
    public void MidnightCrossingWindowMatchesEarlyOnTheFollowingCalendarDay()
    {
        // The exact edge case flagged in the plan: calendar day is Saturday, but the window
        // "belongs" to Friday since that's when it started.
        var windows = new[] { Window("Friday", "22:00", "02:00") };
        Assert.True(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 22, 1, 0))); // Saturday 1am
    }

    [Fact]
    public void MidnightCrossingWindowDoesNotMatchOutsideEitherTail()
    {
        var windows = new[] { Window("Friday", "22:00", "02:00") };
        Assert.False(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 21, 15, 0))); // Friday 3pm
    }

    [Fact]
    public void MidnightCrossingWindowDoesNotBleedIntoSaturdayNightWithoutItsOwnDayFlag()
    {
        // Confirms the "yesterday" check doesn't leak into Saturday itself — a Saturday 11pm
        // segment must not match a Friday-only window.
        var windows = new[] { Window("Friday", "22:00", "02:00") };
        Assert.False(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 22, 23, 0))); // Saturday 11pm
    }

    [Fact]
    public void DisabledDayFlagIsExcluded()
    {
        var windows = new[] { Window("Tuesday", "09:00", "17:00") };
        Assert.False(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 17, 13, 0))); // Monday 1pm
    }

    [Fact]
    public void MultiDayFlagsMatchAnyIncludedDay()
    {
        var windows = new[] { Window("Weekdays", "09:00", "17:00") };
        Assert.True(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 19, 13, 0)));  // Wednesday 1pm
        Assert.False(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 22, 13, 0))); // Saturday 1pm
    }

    [Fact]
    public void AnyMatchingWindowAmongSeveralIsEnough()
    {
        var windows = new[]
        {
            Window("Monday", "09:00", "17:00"),
            Window("Saturday", "10:00", "14:00"),
        };
        Assert.True(NodeWorker.IsWithinSchedule(windows, Local(2026, 8, 22, 12, 0))); // Saturday noon
    }
}
