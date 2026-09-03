using LarisVMS.Core.Enums;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>
/// Covers InferenceProfile — detection/hardware-acceleration overhaul, pass 1. Pure geometry, no
/// ffmpeg/ONNX Runtime involved, so every case here is exact arithmetic checked against hand
/// computation, not just "doesn't throw."
/// </summary>
public class InferenceProfileTests
{
    [Fact]
    public void StretchAlwaysTargetsTheFullNetworkSquareWithNoPad()
    {
        // Deliberately non-square, non-16:9 source — Stretch ignores the source aspect entirely.
        var profile = InferenceProfile.Create(1520, 2688, AspectMode.Stretch);

        Assert.Equal(640, profile.NetworkWidth);
        Assert.Equal(640, profile.NetworkHeight);
        Assert.Equal(640, profile.ScaledWidth);
        Assert.Equal(640, profile.ScaledHeight);
        Assert.Equal(0, profile.PadLeft);
        Assert.Equal(0, profile.PadTop);
    }

    [Fact]
    public void LetterboxOnA16By9LandscapeSourcePadsOnlyTopAndBottom()
    {
        // scale = min(640/1280, 640/720) = 0.5 (width-constrained) -> scaledWidth=640 exactly,
        // scaledHeight=360, so all the padding goes top/bottom, none left/right.
        var profile = InferenceProfile.Create(1280, 720, AspectMode.Letterbox);

        Assert.Equal(640, profile.ScaledWidth);
        Assert.Equal(360, profile.ScaledHeight);
        Assert.Equal(0, profile.PadLeft);
        Assert.Equal(140, profile.PadTop); // (640-360)/2
    }

    [Fact]
    public void LetterboxOnAPortraitSourcePadsOnlyLeftAndRight()
    {
        // The same 16:9 ratio rotated — scale is now height-constrained, padding moves to the
        // opposite axis entirely. This is the exact case a portrait camera (e.g. 1520x2688) needs
        // to not come out squashed the way a plain stretch would.
        var profile = InferenceProfile.Create(720, 1280, AspectMode.Letterbox);

        Assert.Equal(360, profile.ScaledWidth);
        Assert.Equal(640, profile.ScaledHeight);
        Assert.Equal(140, profile.PadLeft); // (640-360)/2
        Assert.Equal(0, profile.PadTop);
    }

    [Fact]
    public void LetterboxOnTheCorridorCameraSubStreamPadsLeftAndRight()
    {
        // The exact shape the AiDetection.Orientation setting exists to produce: a corridor-mounted
        // camera whose Sub stream really is 480x704, where ONVIF advertises 704x480. scale =
        // min(640/480, 640/704) = 640/704 = 0.9090... (height-constrained) -> scaledWidth =
        // 480*0.909 = 436.36 -> 436 after the even-alignment trim, scaledHeight = 640 exactly.
        var profile = InferenceProfile.Create(480, 704, AspectMode.Letterbox);

        Assert.Equal(436, profile.ScaledWidth);
        Assert.Equal(640, profile.ScaledHeight);
        Assert.Equal(102, profile.PadLeft); // (640-436)/2
        Assert.Equal(0, profile.PadTop);
    }

    [Fact]
    public void TheUncorrectedLandscapeReadingOfThatCameraPadsOnTheWrongAxis()
    {
        // What the same camera produced before the orientation setting: ffmpeg was told to scale into
        // a landscape box, so a portrait frame was squashed to fit it. Pinned as a test because the
        // two profiles are mirror images and it is otherwise easy to "fix" one into the other.
        var profile = InferenceProfile.Create(704, 480, AspectMode.Letterbox);

        Assert.Equal(640, profile.ScaledWidth);
        Assert.Equal(436, profile.ScaledHeight);
        Assert.Equal(0, profile.PadLeft);
        Assert.Equal(102, profile.PadTop);
    }

    [Fact]
    public void LetterboxOnAPanoramicSourcePadsTopAndBottomHeavily()
    {
        // A real camera shape from this deployment: 4096x1856. scale = min(640/4096, 640/1856) =
        // 640/4096 = 0.15625 exactly (width-constrained) -> scaledHeight = 1856*0.15625 = 290
        // exactly (already even, no rounding artifact to worry about in this particular case).
        var profile = InferenceProfile.Create(4096, 1856, AspectMode.Letterbox);

        Assert.Equal(640, profile.ScaledWidth);
        Assert.Equal(290, profile.ScaledHeight);
        Assert.Equal(0, profile.PadLeft);
        Assert.Equal(175, profile.PadTop); // (640-290)/2
    }

    [Fact]
    public void ScaledDimensionsAreAlwaysEven()
    {
        // 1001x777 forces a scale that would otherwise round to an odd intermediate size on at
        // least one axis — chroma subsampling requires even dimensions on both.
        var profile = InferenceProfile.Create(1001, 777, AspectMode.Letterbox);

        Assert.Equal(0, profile.ScaledWidth % 2);
        Assert.Equal(0, profile.ScaledHeight % 2);
    }

    [Fact]
    public void DegenerateOnePixelSourceNeverProducesAZeroOrNegativeScaledSize()
    {
        var profile = InferenceProfile.Create(1, 1, AspectMode.Letterbox);

        Assert.True(profile.ScaledWidth >= 2);
        Assert.True(profile.ScaledHeight >= 2);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-5, 100)]
    public void NonPositiveSourceDimensionsThrow(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => InferenceProfile.Create(width, height, AspectMode.Letterbox));
    }

    [Theory]
    [InlineData(639)]
    [InlineData(0)]
    [InlineData(-32)]
    public void NetworkSizeMustBeAPositiveMultipleOf32(int networkSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InferenceProfile.Create(1280, 720, AspectMode.Letterbox, networkSize));
    }

    [Fact]
    public void AspectMatchedIsNotImplementedYet()
    {
        Assert.Throws<NotSupportedException>(() => InferenceProfile.Create(1280, 720, AspectMode.AspectMatched));
    }

    [Fact]
    public void LetterboxRoundTripsABoxBackToItsOriginalSourcePosition()
    {
        var profile = InferenceProfile.Create(1280, 720, AspectMode.Letterbox);

        // An arbitrary box in source-normalized space, comfortably inside the frame.
        const double sx0 = 0.2, sy0 = 0.3, sx1 = 0.6, sy1 = 0.55;

        // Forward transform, computed independently of MapBoxToSource's own implementation: source-
        // normalized -> source-pixel -> network-pixel (scale then pad) -> network-normalized cxcywh,
        // exactly what a real ffmpeg letterbox + D-FINE inference pass would have produced for an
        // object truly sitting at this position in the source frame.
        var scaleX = (double)profile.ScaledWidth / profile.SourceWidth;
        var scaleY = (double)profile.ScaledHeight / profile.SourceHeight;
        var nx0 = sx0 * profile.SourceWidth * scaleX + profile.PadLeft;
        var ny0 = sy0 * profile.SourceHeight * scaleY + profile.PadTop;
        var nx1 = sx1 * profile.SourceWidth * scaleX + profile.PadLeft;
        var ny1 = sy1 * profile.SourceHeight * scaleY + profile.PadTop;
        var cx = (nx0 + nx1) / 2 / profile.NetworkWidth;
        var cy = (ny0 + ny1) / 2 / profile.NetworkHeight;
        var w = (nx1 - nx0) / profile.NetworkWidth;
        var h = (ny1 - ny0) / profile.NetworkHeight;

        var (rx0, ry0, rx1, ry1) = profile.MapBoxToSource(cx, cy, w, h);

        Assert.Equal(sx0, rx0, precision: 9);
        Assert.Equal(sy0, ry0, precision: 9);
        Assert.Equal(sx1, rx1, precision: 9);
        Assert.Equal(sy1, ry1, precision: 9);
    }

    [Theory]
    [InlineData(1280, 720, 1920, 1080)] // 16:9
    [InlineData(704, 576, 1408, 1152)]  // 11:9
    [InlineData(1280, 1024, 640, 512)]  // 5:4
    public void LetterboxGeometryDependsOnlyOnTheAspectRatioNotTheAbsoluteSize(int aw, int ah, int bw, int bh)
    {
        // Pass F builds the engine's profile from the (capped) capture dimensions rather than the raw
        // Sub-stream dimensions. That is only safe because the letterbox scale + pad are a function of
        // the aspect ratio alone — two same-ratio sources must produce byte-identical geometry.
        var a = InferenceProfile.Create(aw, ah, AspectMode.Letterbox);
        var b = InferenceProfile.Create(bw, bh, AspectMode.Letterbox);

        Assert.Equal(a.ScaledWidth, b.ScaledWidth);
        Assert.Equal(a.ScaledHeight, b.ScaledHeight);
        Assert.Equal(a.PadLeft, b.PadLeft);
        Assert.Equal(a.PadTop, b.PadTop);
        Assert.Equal(a.ContentRect, b.ContentRect);
    }

    [Fact]
    public void StretchRoundTripsABoxBackToItsOriginalSourcePosition()
    {
        var profile = InferenceProfile.Create(1920, 1080, AspectMode.Stretch);
        const double sx0 = 0.1, sy0 = 0.4, sx1 = 0.9, sy1 = 0.8;

        // No pad, and scale is per-axis independent — the forward transform is simpler, but the
        // same "compute independently, then invert" approach as the Letterbox round-trip above.
        var cx = (sx0 + sx1) / 2;
        var cy = (sy0 + sy1) / 2;
        var w = sx1 - sx0;
        var h = sy1 - sy0;

        var (rx0, ry0, rx1, ry1) = profile.MapBoxToSource(cx, cy, w, h);

        Assert.Equal(sx0, rx0, precision: 9);
        Assert.Equal(sy0, ry0, precision: 9);
        Assert.Equal(sx1, rx1, precision: 9);
        Assert.Equal(sy1, ry1, precision: 9);
    }
}
