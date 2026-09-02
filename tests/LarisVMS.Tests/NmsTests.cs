using SkiaSharp;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

public class NmsTests
{
    private readonly record struct Candidate(SKRectI Box, double Confidence, string Source);

    private static List<Candidate> Suppress(IReadOnlyList<Candidate> items, double iouThreshold = 0.5)
        => Nms.Suppress(items, c => c.Box, c => c.Confidence, iouThreshold);

    [Fact]
    public void NonOverlappingBoxesAreAllKept()
    {
        var items = new List<Candidate>
        {
            new(new SKRectI(0, 0, 10, 10), 0.9, "a"),
            new(new SKRectI(100, 100, 110, 110), 0.8, "b"),
        };

        var result = Suppress(items);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void WholeFrameBoxBeatsAClippedTileFragmentOfTheSameObject()
    {
        // The motivating scenario: a car near the camera comes back whole from the whole-frame pass
        // (high confidence) and as a clipped fragment ("door") from a tile that only saw part of it
        // (lower confidence, heavily overlapping). The whole-frame box must win.
        var wholeFrameBox = new Candidate(new SKRectI(100, 100, 300, 250), 0.85, "whole-frame");
        var tileFragment = new Candidate(new SKRectI(120, 110, 280, 240), 0.55, "tile");

        var result = Suppress([wholeFrameBox, tileFragment]);

        var kept = Assert.Single(result);
        Assert.Equal("whole-frame", kept.Source);
    }

    [Fact]
    public void ASmallTileOnlyDetectionSurvivesWhenTheWholeFramePassMissedIt()
    {
        var distantObjectFromTile = new Candidate(new SKRectI(500, 500, 520, 515), 0.6, "tile");

        var result = Suppress([distantObjectFromTile]);

        Assert.Single(result);
    }

    [Fact]
    public void TwoGenuinelySeparateObjectsBothSurvive()
    {
        var carFromWholeFrame = new Candidate(new SKRectI(100, 100, 300, 250), 0.85, "whole-frame");
        var pedestrianFromTile = new Candidate(new SKRectI(600, 400, 640, 480), 0.7, "tile");

        var result = Suppress([carFromWholeFrame, pedestrianFromTile]);

        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData(0, 0, 10, 10, 0, 0, 10, 10, 1.0)]   // identical boxes
    [InlineData(0, 0, 10, 10, 20, 20, 30, 30, 0.0)] // no overlap
    [InlineData(0, 0, 10, 10, 5, 0, 15, 10, 0.3333333333333333)] // half-overlap along one axis
    public void IoUMatchesExpectedValue(int aL, int aT, int aR, int aB, int bL, int bT, int bR, int bB, double expected)
    {
        var iou = Nms.IoU(new SKRectI(aL, aT, aR, aB), new SKRectI(bL, bT, bR, bB));
        Assert.Equal(expected, iou, precision: 6);
    }

    [Fact]
    public void FindBestMatchPicksTheCandidateWithHighestOverlap()
    {
        // Checkpoint 3d's own scenario: a whole-frame re-detection pass finds several unrelated
        // objects (a car nearby, a bird elsewhere) alongside the one the trigger was actually about.
        var target = new SKRectI(100, 100, 200, 200);
        var unrelatedCar = new Candidate(new SKRectI(600, 600, 700, 700), 0.9, "car");
        var slightOverlap = new Candidate(new SKRectI(150, 150, 260, 260), 0.7, "partial");
        var strongOverlap = new Candidate(new SKRectI(105, 105, 205, 205), 0.6, "best-match");

        var result = Nms.FindBestMatch([unrelatedCar, slightOverlap, strongOverlap], target, c => c.Box);

        Assert.Equal("best-match", result!.Value.Source);
    }

    [Fact]
    public void FindBestMatchReturnsNullWhenNothingOverlapsAtAll()
    {
        var target = new SKRectI(0, 0, 10, 10);
        var farAway = new Candidate(new SKRectI(500, 500, 600, 600), 0.9, "unrelated");

        var result = Nms.FindBestMatch([farAway], target, c => c.Box);

        Assert.Null(result);
    }
}
