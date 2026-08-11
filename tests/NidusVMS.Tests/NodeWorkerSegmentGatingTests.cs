using NidusVMS.Node;

namespace NidusVMS.Tests;

/// <summary>Covers NodeWorker.ShouldDiscardSegment — the M8 rule deciding whether a segment is
/// discarded (Motion mode, no activity in its pre/post-roll window) or kept/reported, called once
/// per segment after its keep/discard decision has been deferred out to DecideAtUtc (see
/// PendingMotionSegmentDecision). This is the one piece of the recording-mode gating that's
/// actually safe to unit test in isolation; the surrounding deferral timing, file-deletion, and
/// MotionSession-lookup plumbing needs a real camera to verify.
///
/// The bias throughout: default to KEEPING a segment whenever there's any doubt (wrong recording
/// mode string, no motion session available). A false positive here silently deletes real footage,
/// which is categorically worse than a false negative (a Motion-mode camera that behaves like
/// Continuous and just doesn't save the storage it could have).</summary>
public class NodeWorkerSegmentGatingTests
{
    [Fact]
    public void ContinuousModeNeverDiscardsRegardlessOfMotionState()
    {
        Assert.False(NodeWorker.ShouldDiscardSegment("Continuous", hasMotionSession: true, hadMotionInWindow: false));
        Assert.False(NodeWorker.ShouldDiscardSegment("Continuous", hasMotionSession: false, hadMotionInWindow: false));
    }

    [Fact]
    public void MotionModeWithNoMotionSessionNeverDiscards()
    {
        // Misconfigured camera (Motion mode, no zone/no Sub stream) — must fail safe to "keep
        // everything," not "delete everything."
        Assert.False(NodeWorker.ShouldDiscardSegment("Motion", hasMotionSession: false, hadMotionInWindow: false));
        Assert.False(NodeWorker.ShouldDiscardSegment("Motion", hasMotionSession: false, hadMotionInWindow: true));
    }

    [Fact]
    public void MotionModeWithSessionKeepsSegmentsWithinTheWindow()
    {
        Assert.False(NodeWorker.ShouldDiscardSegment("Motion", hasMotionSession: true, hadMotionInWindow: true));
    }

    [Fact]
    public void MotionModeWithSessionDiscardsSegmentsOutsideTheWindow()
    {
        Assert.True(NodeWorker.ShouldDiscardSegment("Motion", hasMotionSession: true, hadMotionInWindow: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("motion")] // wrong case — must not match; an unrecognized string is always Continuous-like
    [InlineData("Schedule")]
    [InlineData("Event")]
    public void UnrecognizedOrUnsupportedModeStringsNeverDiscard(string mode)
    {
        Assert.False(NodeWorker.ShouldDiscardSegment(mode, hasMotionSession: true, hadMotionInWindow: false));
    }
}
