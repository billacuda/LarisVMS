using LarisVMS.Vision.Detection;

namespace LarisVMS.Tests;

public class CompositeFrameScoreTests
{
    private const double Margin = 0.15;

    private static CompositeFrameScore Frame(params (double Confidence, double NormalizedArea)[] boxes)
        => CompositeFrameScore.ForFrame(boxes);

    [Fact]
    public void ForFrameCountsBoxesAndSumsPerBoxScores()
    {
        var score = Frame((0.9, 0.10), (0.8, 0.05));

        Assert.Equal(2, score.MovingCount);
        Assert.Equal(
            MovementClassifier.Score(0.9, 0.10) + MovementClassifier.Score(0.8, 0.05),
            score.CumulativeScore, precision: 10);
    }

    [Fact]
    public void EmptyFrameScoresZero()
    {
        var score = CompositeFrameScore.ForFrame([]);

        Assert.Equal(0, score.MovingCount);
        Assert.Equal(0.0, score.CumulativeScore);
        Assert.Equal(default, score);
    }

    [Fact]
    public void AFrameWithMoreMovingObjectsBeatsALaterFrameWhereTheOneObjectIsLargerAndMoreConfident()
    {
        // The regression this whole change exists for: two medium objects must outrank a later
        // frame that has only one of them, even when that one is now huge and near-certain.
        var twoObjects = Frame((0.75, 0.06), (0.70, 0.05));
        var oneBigObject = Frame((0.99, 0.40));

        Assert.True(oneBigObject.CumulativeScore > twoObjects.CumulativeScore); // score alone would pick the wrong frame
        Assert.False(oneBigObject.BeatsRetained(twoObjects, Margin));
        Assert.True(twoObjects > oneBigObject);
    }

    [Fact]
    public void MoreObjectsAlwaysBeatsFewerRegardlessOfMargin()
    {
        var three = Frame((0.4, 0.02), (0.4, 0.02), (0.4, 0.02));
        var two = Frame((0.9, 0.30), (0.9, 0.30));

        Assert.True(three.BeatsRetained(two, Margin));
    }

    [Fact]
    public void AmongEqualCountFramesTheHigherCumulativeScoreWinsButOnlyPastTheMargin()
    {
        var retained = Frame((0.60, 0.10), (0.60, 0.10));
        var barelyBetter = Frame((0.62, 0.10), (0.62, 0.10)); // ~3% up — jitter, not a real improvement
        var clearlyBetter = Frame((0.90, 0.15), (0.90, 0.15));

        Assert.False(barelyBetter.BeatsRetained(retained, Margin));
        Assert.True(clearlyBetter.BeatsRetained(retained, Margin));
    }

    [Fact]
    public void FewerObjectsNeverBeatsMoreEvenWithAFarHigherCumulativeScore()
    {
        var one = Frame((0.99, 0.60));
        var two = Frame((0.30, 0.02), (0.30, 0.02));

        Assert.False(one.BeatsRetained(two, Margin));
    }

    [Fact]
    public void AnySingleObjectFrameBeatsTheEmptyDefaultRetainedScore()
    {
        var firstSighting = Frame((0.35, 0.01));

        Assert.True(firstSighting.BeatsRetained(default, Margin));
    }

    [Fact]
    public void CompareToOrdersByCountThenCumulativeScore()
    {
        var a = new CompositeFrameScore(1, 100.0);
        var b = new CompositeFrameScore(2, 0.001);
        var c = new CompositeFrameScore(2, 0.002);

        Assert.True(a < b);
        Assert.True(b < c);
        Assert.Equal(c, new[] { a, b, c }.Max());
    }
}
