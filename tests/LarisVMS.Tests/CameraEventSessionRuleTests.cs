using LarisVMS.Core.Dtos;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Covers CameraEventSession.ClassifyRuleEdge — the M8 pass 8 rule deciding whether one
/// ONVIF notification is a configured EventTagRule's rising edge, falling edge, or irrelevant to it.
/// Pulled out of the live PullPoint loop specifically so this is reachable without one, the same
/// reasoning as CameraEventClassifierTests covering IsMotionActive directly.</summary>
public class CameraEventSessionRuleTests
{
    private static readonly Dictionary<string, string> NoItems = new();

    [Fact]
    public void TwoTopicRuleStartTopicIsAlwaysRisingRegardlessOfStateItem()
    {
        var rule = new NodeConfigEventTagRuleDto(Guid.NewGuid(), "tns1:Custom/Start", "tns1:Custom/Stop", DrivesRecording: true);
        Assert.True(CameraEventSession.ClassifyRuleEdge(rule, "tns1:Custom/Start", new Dictionary<string, string> { ["State"] = "false" }));
    }

    [Fact]
    public void TwoTopicRuleStopTopicIsAlwaysFallingRegardlessOfStateItem()
    {
        var rule = new NodeConfigEventTagRuleDto(Guid.NewGuid(), "tns1:Custom/Start", "tns1:Custom/Stop", DrivesRecording: true);
        Assert.False(CameraEventSession.ClassifyRuleEdge(rule, "tns1:Custom/Stop", new Dictionary<string, string> { ["State"] = "true" }));
    }

    [Fact]
    public void TwoTopicRuleUnrelatedTopicIsIrrelevant()
    {
        var rule = new NodeConfigEventTagRuleDto(Guid.NewGuid(), "tns1:Custom/Start", "tns1:Custom/Stop", DrivesRecording: true);
        Assert.Null(CameraEventSession.ClassifyRuleEdge(rule, "tns1:Some/OtherTopic", new Dictionary<string, string> { ["State"] = "true" }));
    }

    [Fact]
    public void ToggleRuleReadsStateItemOffItsOwnTopic()
    {
        var rule = new NodeConfigEventTagRuleDto(Guid.NewGuid(), "tns1:Custom/Toggle", null, DrivesRecording: false);
        Assert.True(CameraEventSession.ClassifyRuleEdge(rule, "tns1:Custom/Toggle", new Dictionary<string, string> { ["State"] = "true" }));
        Assert.False(CameraEventSession.ClassifyRuleEdge(rule, "tns1:Custom/Toggle", new Dictionary<string, string> { ["State"] = "false" }));
    }

    [Fact]
    public void ToggleRuleIgnoresNotificationsOnAnyOtherTopic()
    {
        var rule = new NodeConfigEventTagRuleDto(Guid.NewGuid(), "tns1:Custom/Toggle", null, DrivesRecording: false);
        Assert.Null(CameraEventSession.ClassifyRuleEdge(rule, "tns1:Some/OtherTopic", new Dictionary<string, string> { ["State"] = "true" }));
    }

    // ── Untrustworthy notification timestamps (M8 pass 10) ──────────────────
    // MotionSpans and Segments have to share one timebase — Segments are stamped by ffmpeg on the
    // node, so a message-stamped MotionSpan only lines up if the message's UtcTime agrees. Measured
    // against the live deployment, it often doesn't — and not because the cameras' clocks are wrong
    // (displayed time and NTP sync were both verified correct). A LastClockSynchronization
    // notification carries a timestamp in its payload AND in the message's UtcTime attribute, and on
    // four of six cameras those two disagreed by exactly 60.00 minutes with zero variance across 35+
    // samples: a daylight-saving conversion bug in the camera's ONVIF layer, not drift.

    private static readonly DateTime NodeNow = new(2026, 8, 12, 7, 9, 27, DateTimeKind.Utc);

    [Fact]
    public void ATimestampWithinToleranceIsTrustedAsReported()
    {
        // A camera whose UtcTime is actually right keeps its own (more precise) event instant —
        // delivery latency and ordinary NTP jitter live well inside the tolerance and must not
        // trigger the fallback. Four of the six live cameras are in exactly this state.
        var reported = NodeNow.AddSeconds(-2);
        Assert.Equal(reported, CameraEventSession.ResolveEventTimestamp(reported, NodeNow));
    }

    [Fact]
    public void ATimestampAnHourAheadFallsBackToNodeReceiveTime()
    {
        // The exact live case: the message claimed 08:03:41Z while the node's clock read 07:09:27Z —
        // the DST-conversion hour, not a drifting clock. Trusting it put motion an hour to the right
        // of the footage containing it, and made NodeWorker.DecideMotionSegment's window check
        // unconditionally true (a timestamp an hour in the future satisfies any "did motion happen
        // since X" window), so Motion mode never discarded a single segment.
        var reported = new DateTime(2026, 8, 12, 8, 3, 41, DateTimeKind.Utc);
        Assert.Equal(NodeNow, CameraEventSession.ResolveEventTimestamp(reported, NodeNow));
    }

    [Fact]
    public void ATimestampFarBehindAlsoFallsBackToNodeReceiveTime()
    {
        // Symmetric on purpose — an hour the wrong way (a device on standard time when the server is
        // on DST, rather than the reverse) is just as wrong, and would push motion to the left of its
        // footage instead of the right.
        var reported = NodeNow.AddMinutes(-60);
        Assert.Equal(NodeNow, CameraEventSession.ResolveEventTimestamp(reported, NodeNow));
    }

    [Fact]
    public void AMissingTimestampUsesNodeReceiveTime()
    {
        Assert.Equal(NodeNow, CameraEventSession.ResolveEventTimestamp(null, NodeNow));
    }

    [Fact]
    public void SkewExactlyAtTheToleranceBoundaryIsStillTrusted()
    {
        // Boundary is inclusive — the check rejects only what's genuinely beyond tolerance, so a
        // camera sitting right on the limit doesn't flap between trusted and untrusted.
        var reported = NodeNow.Add(CameraEventSession.MaxTrustedClockSkew);
        Assert.Equal(reported, CameraEventSession.ResolveEventTimestamp(reported, NodeNow));
    }

    [Fact]
    public void ToggleRuleWithNoStateItemAtAllIsFalseNotIrrelevant()
    {
        // Matches CameraEventClassifier's own "missing state means not active" philosophy — a
        // relevant topic with an unparseable payload still resolves to an edge (false), not "skip
        // this notification entirely" the way a wholly unrelated topic does.
        var rule = new NodeConfigEventTagRuleDto(Guid.NewGuid(), "tns1:Custom/Toggle", null, DrivesRecording: false);
        Assert.False(CameraEventSession.ClassifyRuleEdge(rule, "tns1:Custom/Toggle", NoItems));
    }
}
