using NidusVMS.Media;

namespace NidusVMS.Tests;

public class MotionDetectorTests
{
    [Fact]
    public void IdenticalFramesScoreZero()
    {
        var frame = new byte[] { 10, 20, 30, 40 };
        var mask = new[] { true, true, true, true };

        var score = MotionDetector.Score(frame, frame, mask, pixelDeltaThreshold: 25);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void AllPixelsChangedBeyondThresholdScoresOne()
    {
        var previous = new byte[] { 0, 0, 0, 0 };
        var current = new byte[] { 200, 200, 200, 200 };
        var mask = new[] { true, true, true, true };

        var score = MotionDetector.Score(previous, current, mask, pixelDeltaThreshold: 25);

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void OnlyMaskedPixelsCount()
    {
        // 4 pixels, only 2 masked in; both masked pixels change, both unmasked pixels also change
        // (and should be ignored) — score must reflect the masked subset only, not all 4.
        var previous = new byte[] { 0, 0, 0, 0 };
        var current = new byte[] { 200, 200, 200, 200 };
        var mask = new[] { true, true, false, false };

        var score = MotionDetector.Score(previous, current, mask, pixelDeltaThreshold: 25);

        Assert.Equal(1.0, score); // both masked pixels changed -> 2/2
    }

    [Fact]
    public void PartialChangeInsideMaskScoresFraction()
    {
        var previous = new byte[] { 0, 0, 0, 0 };
        var current = new byte[] { 200, 0, 200, 0 }; // 2 of 4 masked pixels changed
        var mask = new[] { true, true, true, true };

        var score = MotionDetector.Score(previous, current, mask, pixelDeltaThreshold: 25);

        Assert.Equal(0.5, score);
    }

    [Fact]
    public void EmptyMaskScoresZeroRatherThanDividingByZero()
    {
        var previous = new byte[] { 0, 0 };
        var current = new byte[] { 255, 255 };
        var mask = new[] { false, false };

        var score = MotionDetector.Score(previous, current, mask, pixelDeltaThreshold: 25);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void DeltaAtExactlyThresholdDoesNotCount()
    {
        // Strictly-greater-than semantics, matching the doc comment ("changed by more than
        // pixelDeltaThreshold") — a delta exactly equal to the threshold is noise-level, not motion.
        var previous = new byte[] { 100 };
        var current = new byte[] { 125 }; // delta == 25
        var mask = new[] { true };

        var score = MotionDetector.Score(previous, current, mask, pixelDeltaThreshold: 25);

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void MismatchedLengthsThrow()
    {
        var previous = new byte[] { 0, 0 };
        var current = new byte[] { 0, 0, 0 };
        var mask = new[] { true, true };

        Assert.Throws<ArgumentException>(() => MotionDetector.Score(previous, current, mask, pixelDeltaThreshold: 25));
    }
}
