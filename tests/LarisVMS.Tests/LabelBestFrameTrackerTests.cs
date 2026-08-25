using LarisVMS.Vision.Detection;

namespace LarisVMS.Tests;

public class LabelBestFrameTrackerTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BestFrame Frame(double x) => new(BaseTime, x, 0, 0.1, 0.1, 0.9);

    [Fact]
    public void NoObservationsMeansNoBestFrame()
    {
        var tracker = new LabelBestFrameTracker();

        Assert.Null(tracker.GetBest("car"));
    }

    [Fact]
    public void NormalSizedCandidateWinsOverAnOversizedOneEvenWithAHigherRawScore()
    {
        // The driveway scenario: a large, well-lit, stable parked truck (oversized box, high
        // confidence — score would win a naive single-slot contest) sharing a label with a smaller
        // passing car (normal-sized box, lower confidence). The car must still be reported.
        var tracker = new LabelBestFrameTracker();

        tracker.Observe("truck", Frame(x: 999), normalizedArea: 0.95, confidence: 0.95);
        tracker.Observe("truck", Frame(x: 1), normalizedArea: 0.2, confidence: 0.5);

        Assert.Equal(1, tracker.GetBest("truck")!.Value.X);
    }

    [Fact]
    public void OversizedCandidateWinsWhenNothingNormalSizedWasEverSeen()
    {
        // A face filling the frame close-up, with nothing smaller ever observed for this span,
        // must still be reported rather than dropped.
        var tracker = new LabelBestFrameTracker();

        tracker.Observe("person", Frame(x: 42), normalizedArea: 0.9, confidence: 0.8);

        Assert.Equal(42, tracker.GetBest("person")!.Value.X);
    }

    [Fact]
    public void HighestScoringNormalCandidateWinsAmongSeveral()
    {
        var tracker = new LabelBestFrameTracker();

        tracker.Observe("car", Frame(x: 1), normalizedArea: 0.1, confidence: 0.3);
        tracker.Observe("car", Frame(x: 2), normalizedArea: 0.2, confidence: 0.9); // highest score
        tracker.Observe("car", Frame(x: 3), normalizedArea: 0.1, confidence: 0.5);

        Assert.Equal(2, tracker.GetBest("car")!.Value.X);
    }

    [Fact]
    public void ResetClearsTheLabelSoALaterSpanStartsFresh()
    {
        var tracker = new LabelBestFrameTracker();
        tracker.Observe("dog", Frame(x: 7), normalizedArea: 0.3, confidence: 0.9);
        Assert.NotNull(tracker.GetBest("dog"));

        tracker.Reset("dog");
        Assert.Null(tracker.GetBest("dog"));

        // A weaker candidate in the next span must win outright — nothing carried over.
        tracker.Observe("dog", Frame(x: 8), normalizedArea: 0.05, confidence: 0.4);
        Assert.Equal(8, tracker.GetBest("dog")!.Value.X);
    }

    [Fact]
    public void LabelsAreTrackedIndependently()
    {
        var tracker = new LabelBestFrameTracker();
        tracker.Observe("car", Frame(x: 1), normalizedArea: 0.2, confidence: 0.9);
        tracker.Observe("truck", Frame(x: 2), normalizedArea: 0.2, confidence: 0.9);

        tracker.Reset("car");

        Assert.Null(tracker.GetBest("car"));
        Assert.NotNull(tracker.GetBest("truck"));
    }
}
