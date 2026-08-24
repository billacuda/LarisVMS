using LarisVMS.Vision.Detection;
using SkiaSharp;

namespace LarisVMS.Tests;

public class MovementClassifierTests
{
    private static readonly DateTime BaseTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ScoreIsMonotonicInConfidenceAndArea()
    {
        var lowConfidence = MovementClassifier.Score(confidence: 0.2, normalizedBoxArea: 0.1);
        var highConfidence = MovementClassifier.Score(confidence: 0.9, normalizedBoxArea: 0.1);
        var smallArea = MovementClassifier.Score(confidence: 0.5, normalizedBoxArea: 0.05);
        var largeArea = MovementClassifier.Score(confidence: 0.5, normalizedBoxArea: 0.5);

        Assert.True(highConfidence > lowConfidence);
        Assert.True(largeArea > smallArea);
    }

    [Fact]
    public void DoublingEitherFactorDoublesTheScore()
    {
        var baseline = MovementClassifier.Score(confidence: 0.3, normalizedBoxArea: 0.2);
        var doubledConfidence = MovementClassifier.Score(confidence: 0.6, normalizedBoxArea: 0.2);
        var doubledArea = MovementClassifier.Score(confidence: 0.3, normalizedBoxArea: 0.4);

        Assert.Equal(baseline * 2, doubledConfidence, precision: 10);
        Assert.Equal(baseline * 2, doubledArea, precision: 10);
    }

    [Fact]
    public void SingleObservationIsAlwaysIdle()
    {
        var classifier = new MovementClassifier();

        var observation = classifier.Observe(trackId: 1, new SKRectI(100, 100, 150, 150),
            confidence: 0.8, frameWidth: 1280, frameHeight: 720, BaseTime);

        Assert.Equal(MovementState.Idle, observation.State);
    }

    [Fact]
    public void StationaryTrackStaysIdle()
    {
        var classifier = new MovementClassifier();
        var box = new SKRectI(100, 100, 150, 150); // 50x50 box, diagonal ~70.7px

        // Same box, several frames within the movement window — no real displacement.
        var last = classifier.Observe(1, box, 0.8, 1280, 720, BaseTime);
        for (var i = 1; i <= 5; i++)
        {
            last = classifier.Observe(1, box, 0.8, 1280, 720, BaseTime.AddMilliseconds(i * 200));
        }

        Assert.Equal(MovementState.Idle, last.State);
    }

    [Fact]
    public void TrackDisplacedBeyondThresholdBecomesMoving()
    {
        var classifier = new MovementClassifier();
        var box = new SKRectI(0, 0, 50, 50); // 50x50 box, diagonal ~70.7px

        classifier.Observe(1, box, 0.8, 1280, 720, BaseTime);

        // Move the box far enough (200px right) that displacement/diagonal well exceeds the
        // default 0.5 threshold, within the 2s movement window.
        var moved = new SKRectI(200, 0, 250, 50);
        var observation = classifier.Observe(1, moved, 0.8, 1280, 720, BaseTime.AddMilliseconds(500));

        Assert.Equal(MovementState.Moving, observation.State);
    }

    [Fact]
    public void DisplacementOutsideTheWindowIsForgotten()
    {
        var classifier = new MovementClassifier(new MovementClassifierOptions
        {
            MovementWindow = TimeSpan.FromSeconds(1),
        });

        classifier.Observe(1, new SKRectI(0, 0, 50, 50), 0.8, 1280, 720, BaseTime);

        // A real displacement, but the gap to the next observation is well past the 1s window —
        // the old sample should have aged out, leaving only the single newest observation, which
        // alone can never register as Moving.
        var observation = classifier.Observe(1, new SKRectI(500, 0, 550, 50), 0.8, 1280, 720,
            BaseTime.AddSeconds(10));

        Assert.Equal(MovementState.Idle, observation.State);
    }

    [Fact]
    public void BestFrameIsNullBeforeAnyObservation()
    {
        var classifier = new MovementClassifier();

        Assert.Null(classifier.GetBestFrame(trackId: 42));
    }

    [Fact]
    public void BestFrameTracksTheHighestScoringObservationSoFar()
    {
        var classifier = new MovementClassifier();

        // First sighting: small box, low confidence — a poor candidate for a snapshot crop.
        classifier.Observe(1, new SKRectI(0, 0, 64, 64), confidence: 0.3, frameWidth: 1280, frameHeight: 720, BaseTime);

        // Later sighting: same track, larger box and higher confidence — this is the one that
        // should win, even though it arrived second. This is the exact scenario the detection
        // plan's verification section calls out: "the later frame wins, not the first one."
        var betterAt = BaseTime.AddSeconds(1);
        classifier.Observe(1, new SKRectI(400, 200, 700, 500), confidence: 0.9, frameWidth: 1280, frameHeight: 720, betterAt);

        var best = classifier.GetBestFrame(1);

        Assert.NotNull(best);
        Assert.Equal(betterAt, best!.Value.AtUtc);
        Assert.Equal(0.9, best.Value.Confidence);
    }

    [Fact]
    public void BestFrameIgnoresAWorseLaterObservation()
    {
        var classifier = new MovementClassifier();

        var goodAt = BaseTime;
        classifier.Observe(1, new SKRectI(400, 200, 700, 500), confidence: 0.9, frameWidth: 1280, frameHeight: 720, goodAt);

        // A later, strictly worse sighting of the same track (smaller box, lower confidence) must
        // not overwrite the earlier, better one.
        classifier.Observe(1, new SKRectI(0, 0, 32, 32), confidence: 0.2, frameWidth: 1280, frameHeight: 720, BaseTime.AddSeconds(1));

        var best = classifier.GetBestFrame(1);

        Assert.NotNull(best);
        Assert.Equal(goodAt, best!.Value.AtUtc);
    }

    [Fact]
    public void BestFrameBoxIsNormalizedToTheObservedFrameDimensions()
    {
        var classifier = new MovementClassifier();

        // 100x50 box inside a 1000x500 frame -> exactly 0.1 x 0.1 normalized.
        classifier.Observe(1, new SKRectI(100, 50, 200, 100), confidence: 0.7, frameWidth: 1000, frameHeight: 500, BaseTime);

        var best = classifier.GetBestFrame(1);

        Assert.NotNull(best);
        Assert.Equal(0.1, best!.Value.X, precision: 10);
        Assert.Equal(0.1, best.Value.Y, precision: 10);
        Assert.Equal(0.1, best.Value.W, precision: 10);
        Assert.Equal(0.1, best.Value.H, precision: 10);
    }

    [Fact]
    public void PruneRemovesTracksNotInTheActiveSet()
    {
        var classifier = new MovementClassifier();

        classifier.Observe(1, new SKRectI(0, 0, 50, 50), 0.8, 1280, 720, BaseTime);
        classifier.Observe(2, new SKRectI(0, 0, 50, 50), 0.8, 1280, 720, BaseTime);

        classifier.Prune(new HashSet<int> { 1 }); // track 2 is gone

        Assert.NotNull(classifier.GetBestFrame(1));
        Assert.Null(classifier.GetBestFrame(2));
    }

    [Fact]
    public void PruneWithEmptyClassifierIsANoOp()
    {
        var classifier = new MovementClassifier();

        classifier.Prune(new HashSet<int>());

        Assert.Null(classifier.GetBestFrame(1));
    }

    [Fact]
    public void DifferentTracksAreClassifiedIndependently()
    {
        var classifier = new MovementClassifier();
        var stationaryBox = new SKRectI(0, 0, 50, 50);

        // Track 1 stays put across several frames.
        classifier.Observe(1, stationaryBox, 0.8, 1280, 720, BaseTime);
        var track1 = classifier.Observe(1, stationaryBox, 0.8, 1280, 720, BaseTime.AddMilliseconds(200));

        // Track 2 moves far in the same window.
        classifier.Observe(2, new SKRectI(0, 0, 50, 50), 0.8, 1280, 720, BaseTime);
        var track2 = classifier.Observe(2, new SKRectI(300, 0, 350, 50), 0.8, 1280, 720, BaseTime.AddMilliseconds(200));

        Assert.Equal(MovementState.Idle, track1.State);
        Assert.Equal(MovementState.Moving, track2.State);
    }
}
