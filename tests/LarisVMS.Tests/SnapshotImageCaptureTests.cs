using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>Covers SnapshotImageCapture.ComputeCropRect — object detection plan decision 10's pure
/// crop-rectangle math (normalized detection box + margin -> clamped pixel rectangle), kept separate
/// from the actual ffmpeg invocation so it's testable without a real ffmpeg binary or segment file.</summary>
public class SnapshotImageCaptureTests
{
    [Fact]
    public void CenteredBoxGetsSymmetricBoxRelativeMarginOnEverySide()
    {
        // A 0.2x0.2 box centered in a 1000x1000 frame, 15% box-relative margin -> 0.03 added on
        // every side. minFrameMarginFraction pinned to 0 to isolate box-relative behavior alone.
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.4, 0.4, 0.2, 0.2, 1000, 1000,
            marginFraction: 0.15, minFrameMarginFraction: 0);

        Assert.Equal(370, x);
        Assert.Equal(370, y);
        Assert.Equal(260, w);
        Assert.Equal(260, h);
    }

    [Fact]
    public void NoMarginOfEitherKindReturnsTheBoxItself()
    {
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.25, 0.25, 0.5, 0.5, 800, 600,
            marginFraction: 0, minFrameMarginFraction: 0);

        Assert.Equal(200, x);
        Assert.Equal(150, y);
        Assert.Equal(400, w);
        Assert.Equal(300, h);
    }

    [Fact]
    public void ASmallBoxGetsTheFrameRelativeMinimumMarginInstead()
    {
        // A tiny 0.01x0.01 box: 30% of that is a negligible 0.003 — far below the 5%-of-frame
        // floor, so the floor must win. This is the exact regression this floor exists to fix: a
        // small/distant object's own box being tiny must not translate into a near-zero absolute
        // crop margin, since the position drift the margin absorbs (Sub-stream detection timing vs
        // the Main-stream recording the crop is actually taken from) doesn't shrink with the box.
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.5, 0.5, 0.01, 0.01, 1000, 1000,
            marginFraction: 0.3, minFrameMarginFraction: 0.05);

        // margin = max(0.01*0.3, 0.05) = 0.05 on every side: x0=0.5-0.05=0.45, x1=0.5+0.01+0.05=0.56.
        Assert.Equal(450, x);
        Assert.Equal(450, y);
        Assert.Equal(110, w);
        Assert.Equal(110, h);
    }

    [Fact]
    public void ALargeBoxKeepsUsingTheBoxRelativeMarginWhenItExceedsTheFloor()
    {
        // A 0.5x0.5 box: 30% of that (0.15) comfortably exceeds the 5% frame floor, so the
        // box-relative margin should still be the one that wins.
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.5, 0.5, 0.5, 0.5, 1000, 1000,
            marginFraction: 0.3, minFrameMarginFraction: 0.05);

        // margin = max(0.5*0.3, 0.05) = 0.15: x0=0.5-0.15=0.35, x1=min(0.5+0.5+0.15,1)=1.0 (clamped).
        Assert.Equal(350, x);
        Assert.Equal(350, y);
        Assert.Equal(650, w);
        Assert.Equal(650, h);
    }

    [Fact]
    public void DefaultConstantsProduceTheExpectedMargin()
    {
        // Pins the actual shipped defaults (not just the mechanism above) against a regression.
        var (x, _, w, _) = SnapshotImageCapture.ComputeCropRect(0.5, 0.5, 0.2, 0.2, 1000, 1000);

        // margin = max(0.2*0.3, 0.05) = 0.06.
        Assert.Equal(440, x);
        Assert.Equal(320, w);
    }

    [Fact]
    public void BoxAgainstTheTopLeftEdgeClampsMarginRatherThanGoingNegative()
    {
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.0, 0.0, 0.1, 0.1, 1000, 1000);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.True(w > 0 && h > 0);
    }

    [Fact]
    public void BoxAgainstTheBottomRightEdgeClampsWithinFrameBounds()
    {
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.9, 0.9, 0.1, 0.1, 1000, 1000);

        Assert.True(x + w <= 1000);
        Assert.True(y + h <= 1000);
    }

    [Fact]
    public void FullFrameBoxClampsToFrameDimensions()
    {
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.0, 0.0, 1.0, 1.0, 640, 480);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.True(w <= 640);
        Assert.True(h <= 480);
    }

    [Fact]
    public void DegenerateNearZeroBoxStillProducesACroppableRectangle()
    {
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.5, 0.5, 0.0001, 0.0001, 1920, 1080);

        Assert.True(w >= 2);
        Assert.True(h >= 2);
        Assert.True(x >= 0 && x < 1920);
        Assert.True(y >= 0 && y < 1080);
        // The frame-relative floor should still give a near-zero box meaningfully more than the
        // bare ffmpeg-positive-size minimum — a razor-thin sliver around "basically a point" isn't
        // useful, which is exactly the regression DefaultMinFrameMarginFraction exists to prevent.
        Assert.True(w > 50 && h > 50);
    }
}
