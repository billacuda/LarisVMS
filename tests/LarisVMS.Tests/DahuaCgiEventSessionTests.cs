using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Core.Enums;
using LarisVMS.Media;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Drives the Dahua plugin session's real classify/hysteresis/span logic straight from raw
/// event lines, with no socket involved — the connection loop is thin, but this behavior is what
/// determines whether a person walking past actually produces a span, and it can't be exercised
/// against a camera from a test run.</summary>
public class DahuaCgiEventSessionTests
{
    private static (DahuaCgiEventSession Session, List<(DetectionKind Kind, MotionSpanResult Span)> Spans) NewSession()
    {
        var session = new DahuaCgiEventSession(
            new Uri("http://camera.example"), "user", "pass", NullLogger.Instance);
        var spans = new List<(DetectionKind, MotionSpanResult)>();
        session.DetectionSpanCompleted += (kind, span) => spans.Add((kind, span));
        return (session, spans);
    }

    private static readonly DateTime T0 = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AStartThenStopProducesOneClosedSpan()
    {
        var (session, spans) = NewSession();

        session.HandleLine("Code=SmartMotionHuman;action=Start;index=0", T0);
        Assert.Empty(spans); // still open — nothing to report yet
        Assert.True(session.AnyDetectionActive);

        session.HandleLine("Code=SmartMotionHuman;action=Stop;index=0", T0.AddSeconds(12));

        var (kind, span) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Human, kind);
        Assert.Equal(T0, span.StartUtc);
        Assert.Equal(T0.AddSeconds(12), span.EndUtc);
        Assert.False(session.AnyDetectionActive);
    }

    /// <summary>A real CrossRegionDetection payload as this fleet's IP8M cameras emit it — one IVS
    /// intrusion rule configured for Human *and* Vehicle, so the code alone can't say which was seen
    /// and only the payload can. Pretty-printed across lines exactly like the camera sends it, which
    /// is the whole reason the session has to accumulate rather than parse line by line.</summary>
    private static string[] IntrusionEventLines(string action, string objectType) =>
    [
        $"Code=CrossRegionDetection;action={action};index=0;data={{",
        "   \"Class\" : \"Normal\",",
        "   \"Name\" : \"IVS-1\",",
        "   \"Object\" : {",
        "      \"Action\" : \"Appear\",",
        "      \"BoundingBox\" : [ 3104, 2688, 4288, 5104 ],",
        "      \"Confidence\" : 0,",
        $"      \"ObjectType\" : \"{objectType}\"",
        "   },",
        "   \"RuleId\" : 1",
        "}"
    ];

    /// <summary>A verbatim CrossRegionDetection payload captured from a real IP8M-DLB2998EW-AI with an
    /// IVS intrusion rule active — kept exactly as the camera sends it rather than tidied, because the
    /// things most likely to break the accumulator are precisely the messy parts: three levels of
    /// nested braces (Object → FusionInfo → GpsPostion), a bracketed DetectRegion array whose square
    /// brackets must NOT be counted as braces, and the object detail sitting under a singular `Object`
    /// key rather than the `Objects[]` array the FaceDetection payload used.</summary>
    private static string[] RealIntrusionEventLines(string action) =>
    [
        $"Code=CrossRegionDetection;action={action};index=0;data={{",
        "   \"Action\" : \"Appear\",",
        "   \"Class\" : \"Normal\",",
        "   \"CountInGroup\" : 1,",
        "   \"DetectRegion\" : [",
        "      [ 1771, 1211 ],",
        "      [ 7314, 1337 ],",
        "      [ 7276, 5599 ],",
        "      [ 1866, 6310 ]",
        "   ],",
        "   \"EventID\" : 10013,",
        "   \"GroupID\" : 8,",
        "   \"Name\" : \"IVS-1\",",
        "   \"Object\" : {",
        "      \"Action\" : \"Appear\",",
        "      \"BelongID\" : 0,",
        "      \"BoundingBox\" : [ 4464, 960, 4928, 3888 ],",
        "      \"Center\" : [ 4696, 2424 ],",
        "      \"Confidence\" : 0,",
        "      \"FusionInfo\" : {",
        "         \"BelongID\" : 0,",
        "         \"GpsPosValid\" : 0,",
        "         \"GpsPostion\" : {",
        "            \"Latitude\" : 0.0,",
        "            \"Longitude\" : 0.0",
        "         },",
        "         \"ObjectSource\" : 0",
        "      },",
        "      \"LowerBodyColor\" : [ 0, 0, 0, 0 ],",
        "      \"ObjectID\" : 105,",
        "      \"ObjectType\" : \"Human\",",
        "      \"RelativeID\" : 0,",
        "      \"humanTripLineDirection\" : 0",
        "   },",
        "   \"RealUTC\" : 1787286725,",
        "   \"RuleID\" : 4,",
        "   \"Track\" : [],",
        "   \"UTCMS\" : 2",
        "}"
    ];

    [Fact]
    public void ARealCapturedIntrusionEventClassifiesAsPerson()
    {
        var (session, spans) = NewSession();

        foreach (var line in RealIntrusionEventLines("Start")) session.HandleLine(line, T0);
        Assert.Empty(spans); // nested braces must not have closed the event early
        Assert.True(session.AnyDetectionActive);

        foreach (var line in RealIntrusionEventLines("Stop")) session.HandleLine(line, T0.AddSeconds(3));

        var (kind, span) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Human, kind);
        Assert.Equal(T0, span.StartUtc);
        Assert.Equal(T0.AddSeconds(3), span.EndUtc);
    }

    [Fact]
    public void AnUnsubscribedCodeInterleavedBetweenEventsIsIgnored()
    {
        // The real stream interleaves IntelliFrame pulses between intrusion events. We never subscribe
        // to it, but it must be harmless if it ever does arrive — and in particular must not be
        // mistaken for a detection or left half-accumulated.
        var (session, spans) = NewSession();

        foreach (var line in RealIntrusionEventLines("Start")) session.HandleLine(line, T0);
        session.HandleLine("Code=IntelliFrame;action=Pulse;index=0;data={", T0);
        session.HandleLine("   \"Action\" : \"Start\",", T0);
        session.HandleLine("   \"RealUTC\" : 1787286725", T0);
        session.HandleLine("}", T0);
        foreach (var line in RealIntrusionEventLines("Stop")) session.HandleLine(line, T0.AddSeconds(3));

        var (kind, _) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Human, kind);
    }

    [Fact]
    public void AnIntrusionEventIsClassifiedFromItsPayloadObjectType()
    {
        // The point of the whole ObjectType pass: CrossRegionDetection maps to Other by code, but a
        // payload naming Human must produce Person instead.
        var (session, spans) = NewSession();

        foreach (var line in IntrusionEventLines("Start", "Human")) session.HandleLine(line, T0);
        foreach (var line in IntrusionEventLines("Stop", "Human")) session.HandleLine(line, T0.AddSeconds(6));

        var (kind, _) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Human, kind);
    }

    [Fact]
    public void TheSameIntrusionRuleReportsVehicleSeparatelyFromPerson()
    {
        // One rule, one code, two classes — they must not collapse into a single span.
        var (session, spans) = NewSession();

        foreach (var line in IntrusionEventLines("Start", "Vehicle")) session.HandleLine(line, T0);
        foreach (var line in IntrusionEventLines("Stop", "Vehicle")) session.HandleLine(line, T0.AddSeconds(4));

        var (kind, _) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Vehicle, kind);
    }

    [Fact]
    public void AnIntrusionEventWithNoObjectTypeFallsBackToTheCodeMapping()
    {
        // Firmware that publishes no object detail must keep behaving exactly as before this pass.
        var (session, spans) = NewSession();

        session.HandleLine("Code=CrossRegionDetection;action=Start;index=0", T0);
        session.HandleLine("Code=CrossRegionDetection;action=Stop;index=0", T0.AddSeconds(3));

        var (kind, _) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Other, kind);
    }

    [Fact]
    public void BoundaryMarkersAndHeadersBetweenEventsAreIgnored()
    {
        // The real stream interleaves multipart boundaries and headers between events; they must not
        // corrupt the accumulator or be mistaken for events.
        var (session, spans) = NewSession();

        foreach (var line in IntrusionEventLines("Start", "Human")) session.HandleLine(line, T0);
        session.HandleLine("", T0);
        session.HandleLine("--myboundary", T0);
        session.HandleLine("Content-Type: text/plain", T0);
        session.HandleLine("Content-Length: 152", T0);
        session.HandleLine("", T0);
        foreach (var line in IntrusionEventLines("Stop", "Human")) session.HandleLine(line, T0.AddSeconds(8));

        var (kind, span) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Human, kind);
        Assert.Equal(T0.AddSeconds(8), span.EndUtc);
    }

    [Fact]
    public void AFaceEventStillClassifiesAsFaceFromItsHumanFaceObjectType()
    {
        // HumanFace must stay Face rather than being folded into Person — a face event and a
        // whole-body detection are different things, and CodeMap already distinguished them.
        var (session, spans) = NewSession();

        session.HandleLine("Code=FaceDetection;action=Start;index=0;data={", T0);
        session.HandleLine("   \"Object\" : { \"ObjectType\" : \"HumanFace\" }", T0);
        session.HandleLine("}", T0);
        session.HandleLine("Code=FaceDetection;action=Stop;index=0", T0.AddSeconds(2));

        var (kind, _) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Face, kind);
    }

    [Fact]
    public void APulseOpensAndClosesInOneEvent()
    {
        // Codes that only ever fire momentarily would otherwise leave a span open until shutdown.
        var (session, spans) = NewSession();

        session.HandleLine("Code=CrossLineDetection;action=Pulse;index=0", T0);

        var (kind, _) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Other, kind);
        Assert.False(session.AnyDetectionActive);
    }

    [Fact]
    public void PersonAndVehicleTrackIndependently()
    {
        // A car leaving must not close the span for the person still standing there.
        var (session, spans) = NewSession();

        session.HandleLine("Code=SmartMotionHuman;action=Start;index=0", T0);
        session.HandleLine("Code=SmartMotionVehicle;action=Start;index=0", T0.AddSeconds(1));
        session.HandleLine("Code=SmartMotionVehicle;action=Stop;index=0", T0.AddSeconds(5));

        var (kind, _) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Vehicle, kind);
        Assert.True(session.AnyDetectionActive); // the person is still there
    }

    [Fact]
    public void AnUnmappedCodeIsIgnoredEntirely()
    {
        var (session, spans) = NewSession();

        session.HandleLine("Code=StorageFailure;action=Start;index=0", T0);
        session.HandleLine("Code=VideoMotion;action=Start;index=0", T0);

        Assert.Empty(spans);
        Assert.False(session.AnyDetectionActive);
    }

    [Theory]
    [InlineData("--myboundary")]
    [InlineData("Content-Length: 42")]
    [InlineData("")]
    public void NonEventLinesAreHarmless(string line)
    {
        var (session, spans) = NewSession();

        session.HandleLine(line, T0);

        Assert.Empty(spans);
        Assert.False(session.AnyDetectionActive);
    }

    [Fact]
    public void AnOpenDetectionSatisfiesTheRecordingKeepCheck()
    {
        // This is what makes Motion-mode recording retain footage for a person the camera can see —
        // both the "recently seen" and the "still open, however sparsely reported" halves.
        var (session, _) = NewSession();

        session.HandleLine("Code=SmartMotionHuman;action=Start;index=0", T0);

        Assert.True(session.AnyDetectionSince(T0.AddSeconds(-30)));
        Assert.True(session.AnyDetectionActive);
        // Deliberately still true long after the last notification: these cameras report one edge
        // and then go quiet, so a staleness window would drop footage mid-event.
        Assert.True(session.AnyDetectionActive);
    }

    [Fact]
    public void AnOpenSpanIsCheckpointedWhileStillInProgress()
    {
        var (session, _) = NewSession();
        session.HandleLine("Code=SmartMotionHuman;action=Start;index=0", T0);

        var inProgress = session.CurrentInProgressDetectionSpans(T0.AddMinutes(2)).ToList();

        var (kind, span) = Assert.Single(inProgress);
        Assert.Equal(DetectionKind.Human, kind);
        Assert.Equal(T0, span.StartUtc);
    }

    [Fact]
    public void ADroppedConnectionClosesAnOpenSpanInsteadOfLeavingItRunning()
    {
        // Reconnecting can't recover the missed Stop: attach only delivers events from the moment it
        // subscribes. Left open, the 15s checkpoint loop would keep extending EndUtc forever, so a
        // camera reboot while someone is in frame would read as a person standing there for hours.
        var (session, spans) = NewSession();
        session.HandleLine("Code=SmartMotionHuman;action=Start;index=0", T0);

        session.FlushOpenSpans(T0.AddSeconds(20)); // connection dropped here

        var (kind, span) = Assert.Single(spans);
        Assert.Equal(DetectionKind.Human, kind);
        Assert.Equal(T0, span.StartUtc);
        Assert.Equal(T0.AddSeconds(20), span.EndUtc);
        Assert.False(session.AnyDetectionActive);
        Assert.Empty(session.CurrentInProgressDetectionSpans(T0.AddMinutes(5)));
    }

    [Fact]
    public void AStartAfterReconnectOpensANewSpanRatherThanExtendingTheOldOne()
    {
        var (session, spans) = NewSession();
        session.HandleLine("Code=SmartMotionHuman;action=Start;index=0", T0);
        session.FlushOpenSpans(T0.AddSeconds(20));

        // Feed comes back and the person is still there, so the camera sends a fresh Start.
        session.HandleLine("Code=SmartMotionHuman;action=Start;index=0", T0.AddSeconds(50));
        session.HandleLine("Code=SmartMotionHuman;action=Stop;index=0", T0.AddSeconds(70));

        Assert.Equal(2, spans.Count);
        Assert.Equal(T0.AddSeconds(20), spans[0].Span.EndUtc);
        Assert.Equal(T0.AddSeconds(50), spans[1].Span.StartUtc); // a gap where the feed was down
        Assert.Equal(T0.AddSeconds(70), spans[1].Span.EndUtc);
    }

    [Fact]
    public void FlushingWithNothingOpenEmitsNothing()
    {
        // Called on every reconnect, including ones where no detection was in progress.
        var (session, spans) = NewSession();

        session.FlushOpenSpans(T0);
        session.HandleLine("Code=SmartMotionHuman;action=Start;index=0", T0.AddSeconds(5));
        session.HandleLine("Code=SmartMotionHuman;action=Stop;index=0", T0.AddSeconds(9));
        session.FlushOpenSpans(T0.AddSeconds(30));

        Assert.Single(spans); // just the one real span, no empty flush artifacts
    }

    [Fact]
    public void TheAttachUrlRequestsOnlyActionableCodes()
    {
        var (session, _) = NewSession();

        var uri = session.BuildAttachUri().ToString();

        Assert.Contains("/cgi-bin/eventManager.cgi", uri);
        Assert.Contains("action=attach", uri);
        Assert.Contains("SmartMotionHuman", uri);
        // [All] would also stream every heartbeat/storage/config event the camera produces.
        Assert.DoesNotContain("codes=[All]", uri);
    }

    [Fact]
    public void TheAttachUrlIsBuiltFromTheCamerasOwnSchemeHostAndPort()
    {
        var session = new DahuaCgiEventSession(
            new Uri("https://10.0.0.5:8443"), "u", "p", NullLogger.Instance);

        var uri = session.BuildAttachUri();

        Assert.Equal("https", uri.Scheme);
        Assert.Equal(8443, uri.Port);
        Assert.Equal("10.0.0.5", uri.Host);
    }
}
