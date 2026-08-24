using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>Covers SnapshotImageCapture.ComputeCropRect — object detection plan decision 10's pure
/// crop-rectangle math (normalized detection box + margin -> clamped pixel rectangle), kept separate
/// from the actual ffmpeg invocation so it's testable without a real ffmpeg binary or segment file.</summary>
public class SnapshotImageCaptureTests
{
    [Fact]
    public void CenteredBoxGetsSymmetricMarginOnEverySide()
    {
        // A 0.2x0.2 box centered in a 1000x1000 frame, 15% margin -> 0.03 added on every side.
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.4, 0.4, 0.2, 0.2, 1000, 1000);

        Assert.Equal(370, x);
        Assert.Equal(370, y);
        Assert.Equal(260, w);
        Assert.Equal(260, h);
    }

    [Fact]
    public void NoMarginReturnsTheBoxItself()
    {
        var (x, y, w, h) = SnapshotImageCapture.ComputeCropRect(0.25, 0.25, 0.5, 0.5, 800, 600, marginFraction: 0);

        Assert.Equal(200, x);
        Assert.Equal(150, y);
        Assert.Equal(400, w);
        Assert.Equal(300, h);
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
    }
}
