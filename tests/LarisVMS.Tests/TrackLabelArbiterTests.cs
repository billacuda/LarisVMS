using LarisVMS.Vision.Detection;

namespace LarisVMS.Tests;

public class TrackLabelArbiterTests
{
    [Fact]
    public void FirstObservationWinsOutright()
    {
        var arbiter = new TrackLabelArbiter();

        Assert.Equal("cat", arbiter.Resolve(trackId: 1, "cat", confidence: 0.6));
    }

    [Fact]
    public void FlickerAcrossSeveralLabelsResolvesToTheSingleHighestConfidenceOne()
    {
        // The motivating scenario: one physical object read as cat -> dog -> cow -> horse across a
        // few frames. The track must resolve to whichever label peaked highest, not the most recent.
        var arbiter = new TrackLabelArbiter();

        arbiter.Resolve(1, "cat", confidence: 0.55);
        arbiter.Resolve(1, "dog", confidence: 0.50);
        arbiter.Resolve(1, "cow", confidence: 0.95); // clear peak
        var resolved = arbiter.Resolve(1, "horse", confidence: 0.40);

        Assert.Equal("cow", resolved);
    }

    [Fact]
    public void ASustainedHigherConfidenceChallengerDoesWinASwitch()
    {
        var arbiter = new TrackLabelArbiter();

        arbiter.Resolve(1, "dog", confidence: 0.50);
        // Challenger beats the incumbent's peak by more than SwitchMargin (0.10).
        var resolved = arbiter.Resolve(1, "cat", confidence: 0.50 + TrackLabelArbiter.SwitchMargin + 0.01);

        Assert.Equal("cat", resolved);
    }

    [Fact]
    public void AMarginalChallengerInsideTheMarginDoesNotSwitchTheWinner()
    {
        var arbiter = new TrackLabelArbiter();

        arbiter.Resolve(1, "dog", confidence: 0.50);
        // Challenger beats the incumbent's peak, but not by more than SwitchMargin.
        var resolved = arbiter.Resolve(1, "cat", confidence: 0.50 + TrackLabelArbiter.SwitchMargin - 0.01);

        Assert.Equal("dog", resolved);
    }

    [Fact]
    public void TwoSimultaneousTracksArbitrateIndependently()
    {
        var arbiter = new TrackLabelArbiter();

        arbiter.Resolve(1, "cat", confidence: 0.9);
        arbiter.Resolve(2, "dog", confidence: 0.9);

        Assert.Equal("cat", arbiter.Resolve(1, "cat", confidence: 0.1));
        Assert.Equal("dog", arbiter.Resolve(2, "dog", confidence: 0.1));
    }

    [Fact]
    public void PruningReleasesATracksStateSoItStartsFreshOnItsNextResolve()
    {
        var arbiter = new TrackLabelArbiter();

        arbiter.Resolve(1, "cow", confidence: 0.95);
        arbiter.Resolve(1, "horse", confidence: 0.10); // would not win against cow's peak

        arbiter.Prune(new HashSet<int>()); // track 1 no longer active

        // A brand-new observation for the same (reused) track id starts a fresh contest — the old
        // "cow" peak must not still be sitting there to beat it.
        var resolved = arbiter.Resolve(1, "horse", confidence: 0.10);
        Assert.Equal("horse", resolved);
    }
}
