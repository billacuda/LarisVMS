using LarisVMS.Core;
using LarisVMS.Core.Enums;

namespace LarisVMS.Tests;

/// <summary>Covers the Dahua/Amcrest CGI event grammar and code mapping. These carry most of the
/// weight for that integration: the transport is a plain line reader, but the payload shape is
/// vendor-specific and varies by firmware, and it can't be exercised against real hardware from a
/// test run — so the sample lines here are the contract.</summary>
public class DahuaCgiEventParserTests
{
    [Fact]
    public void ParsesATypicalEventLine()
    {
        var parsed = DahuaCgiEventParser.ParseLine("Code=SmartMotionHuman;action=Start;index=0");

        Assert.NotNull(parsed);
        Assert.Equal("SmartMotionHuman", parsed!.Value.Code);
        Assert.Equal("Start", parsed.Value.Action);
        Assert.Equal(0, parsed.Value.Index);
        Assert.True(parsed.Value.IsStart);
        Assert.False(parsed.Value.IsPulse);
    }

    [Fact]
    public void ParsesAStopAsNotStarted()
    {
        var parsed = DahuaCgiEventParser.ParseLine("Code=SmartMotionHuman;action=Stop;index=0");

        Assert.False(parsed!.Value.IsStart);
    }

    [Fact]
    public void APulseCountsAsAStart()
    {
        // Some codes only ever fire momentarily, with no matching Stop — the session opens and
        // closes the span together rather than leaving it open forever.
        var parsed = DahuaCgiEventParser.ParseLine("Code=CrossLineDetection;action=Pulse;index=0");

        Assert.True(parsed!.Value.IsPulse);
        Assert.True(parsed.Value.IsStart);
    }

    [Fact]
    public void ReadsANonZeroChannelIndex()
    {
        var parsed = DahuaCgiEventParser.ParseLine("Code=SmartMotionVehicle;action=Start;index=3");

        Assert.Equal(3, parsed!.Value.Index);
    }

    [Fact]
    public void IgnoresATrailingDataBlobIncludingItsSemicolons()
    {
        // data= is a JSON blob whose shape varies by code and firmware, and can itself contain ';'.
        // It must never corrupt the fields that matter.
        var parsed = DahuaCgiEventParser.ParseLine(
            "Code=SmartMotionHuman;action=Start;index=0;data={\"Object\":{\"Rect\":[1;2;3;4]}}");

        Assert.Equal("SmartMotionHuman", parsed!.Value.Code);
        Assert.Equal("Start", parsed.Value.Action);
        Assert.Equal(0, parsed.Value.Index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("--myboundary")]
    [InlineData("Content-Type: text/plain")]
    [InlineData("Content-Length: 40")]
    [InlineData("Code=SmartMotionHuman")]        // no action: says nothing about an edge
    [InlineData("action=Start;index=0")]          // no code
    public void IgnoresEverythingThatIsNotACompleteEventLine(string? line)
    {
        Assert.Null(DahuaCgiEventParser.ParseLine(line));
    }

    [Fact]
    public void ParsesTheWholeMultipartFrameAsTheCameraSendsIt()
    {
        // Fed line by line exactly as the reader does, boundaries and headers included.
        var body = """
            --myboundary
            Content-Type: text/plain
            Content-Length: 42

            Code=SmartMotionHuman;action=Start;index=0
            --myboundary
            Content-Type: text/plain
            Content-Length: 41

            Code=SmartMotionHuman;action=Stop;index=0
            """;

        var events = body.Split('\n')
            .Select(DahuaCgiEventParser.ParseLine)
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToList();

        Assert.Equal(2, events.Count);
        Assert.True(events[0].IsStart);
        Assert.False(events[1].IsStart);
    }

    [Theory]
    [InlineData("SmartMotionHuman", DetectionKind.Human)]
    [InlineData("HumanDetect", DetectionKind.Human)]
    [InlineData("SmartMotionVehicle", DetectionKind.Vehicle)]
    [InlineData("FaceDetection", DetectionKind.Face)]
    [InlineData("CrossLineDetection", DetectionKind.Other)]
    [InlineData("smartmotionhuman", DetectionKind.Human)] // case-insensitive
    public void MapsVendorCodesToWhatWasSeen(string code, DetectionKind expected)
    {
        Assert.Equal(expected, DahuaCgiEventParser.Classify(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("VideoMotion")]        // plain motion: already covered by ONVIF, deliberately unmapped
    [InlineData("StorageFailure")]
    [InlineData("SomeFutureCode")]
    public void LeavesUnmodelledCodesUnclassified(string? code)
    {
        Assert.Null(DahuaCgiEventParser.Classify(code));
    }

    [Fact]
    public void SubscribeCodesCoversEveryMappedCodeAndNothingElse()
    {
        var codes = DahuaCgiEventParser.SubscribeCodes.Split(',');

        Assert.Contains("SmartMotionHuman", codes);
        Assert.Contains("SmartMotionVehicle", codes);
        Assert.Contains("CrossLineDetection", codes);
        Assert.Contains("CrossRegionDetection", codes);
        // Subscribing to plain motion would double-report what ONVIF already delivers. Checked as an
        // exact element rather than a substring so it doesn't accidentally reject VideoMotionInfo,
        // which is a different code and is subscribed on purpose (see below).
        Assert.DoesNotContain("VideoMotion", codes);
        Assert.DoesNotContain("All", codes);

        // Every code is either one this app can act on, or a deliberate keep-alive. The keep-alive
        // must classify as nothing, so it can never be mistaken for a detection — that property is
        // what makes subscribing to it safe.
        Assert.Contains("VideoMotionInfo", codes);
        Assert.Null(DahuaCgiEventParser.Classify("VideoMotionInfo"));
        Assert.All(
            codes.Where(c => c != "VideoMotionInfo"),
            code => Assert.NotNull(DahuaCgiEventParser.Classify(code)));
    }

    [Fact]
    public void AKeepAliveEventProducesNoDetection()
    {
        // The real shape the camera sends — a bare State ping with only a timestamp. It exists to
        // keep the connection from looking dead, and must produce nothing else.
        var evt = DahuaCgiEventParser.ParseEvent(
            "Code=VideoMotionInfo;action=State;index=0;data={\n   \"RealUTC\" : 1787288021\n}");

        Assert.NotNull(evt);
        Assert.Equal("VideoMotionInfo", evt!.Value.Code);
        Assert.Null(DahuaCgiEventParser.Classify(evt.Value.Code));
        Assert.Null(DahuaCgiEventParser.ClassifyObjectType(evt.Value.ObjectType));
    }

    [Theory]
    [InlineData("AnimalDetection", DetectionKind.Animal)]
    [InlineData("SmartMotionAnimal", DetectionKind.Animal)]
    [InlineData("PetDetection", DetectionKind.Animal)]
    public void AnimalCodesClassifyAsAnimal(string code, DetectionKind expected)
        => Assert.Equal(expected, DahuaCgiEventParser.Classify(code));

    [Theory]
    // The abandoned-object pair. Both used to land in Other, which lost the distinction between
    // something being left behind and something being taken.
    [InlineData("LeftDetection", DetectionKind.ObjectAppeared)]
    [InlineData("AbandonedObjectDetection", DetectionKind.ObjectAppeared)]
    [InlineData("TakenAwayDetection", DetectionKind.ObjectMissing)]
    [InlineData("MissingObjectDetection", DetectionKind.ObjectMissing)]
    public void LeftAndTakenAwayAreDistinctClasses(string code, DetectionKind expected)
        => Assert.Equal(expected, DahuaCgiEventParser.Classify(code));
}
