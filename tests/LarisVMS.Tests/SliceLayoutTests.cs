using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

public class SliceLayoutTests
{
    [Fact]
    public void SixteenByNine_ScalesShortEdgeAndProducesTwoOverlappingSlices()
    {
        // 1920x1080 -> short edge (1080) to 640, long edge scaled by the same factor:
        // 1920 * (640/1080) = 1137.78 -> rounds to 1138 -> even-aligned down to 1138 (already even).
        var layout = SliceLayout.Create(1920, 1080, 640);

        Assert.True(layout.IsLandscape);
        Assert.Equal(1138, layout.CaptureWidth);
        Assert.Equal(640, layout.CaptureHeight);
        Assert.Equal(2, layout.Slices.Count);
        Assert.Equal(new SliceLayout.Tile(0, 0, 640, 640), layout.Slices[0]);
        Assert.Equal(new SliceLayout.Tile(498, 0, 640, 640), layout.Slices[1]); // 1138 - 640
    }

    [Fact]
    public void PortraitSource_MirrorsTheLandscapeCase()
    {
        var layout = SliceLayout.Create(1080, 1920, 640);

        Assert.False(layout.IsLandscape);
        Assert.Equal(640, layout.CaptureWidth);
        Assert.Equal(1138, layout.CaptureHeight);
        Assert.Equal(2, layout.Slices.Count);
        Assert.Equal(new SliceLayout.Tile(0, 0, 640, 640), layout.Slices[0]);
        Assert.Equal(new SliceLayout.Tile(0, 498, 640, 640), layout.Slices[1]);
    }

    [Fact]
    public void ASquareOrNearSquareSourceStillGetsTwoRealSlices()
    {
        // 4:3 is close to square relative to how wide these cameras get, but still must not collapse
        // to a single slice — that would just be today's Stretch/Letterbox behavior with none of the
        // "more pixels on target" benefit this mode exists for.
        var layout = SliceLayout.Create(640, 640, 640);

        Assert.Equal(2, layout.Slices.Count);
        // A truly square source has nothing to scale and no long edge to spread slices across —
        // both slices land at the same origin (full overlap), which is a degenerate but harmless case:
        // SliceMerge's IoU-based path collapses them back into one detection per real object.
        Assert.Equal(layout.Slices[0], layout.Slices[1]);
    }

    [Theory]
    [InlineData(1920, 1080)] // 16:9
    [InlineData(1280, 960)]  // 4:3
    [InlineData(3840, 1080)] // ~32:9 panoramic
    [InlineData(704, 480)]   // a corridor camera's typical landscape report
    public void EverySliceFitsFullyInsideTheCaptureBuffer(int width, int height)
    {
        var layout = SliceLayout.Create(width, height, 640);

        foreach (var tile in layout.Slices)
        {
            Assert.True(tile.X >= 0 && tile.Y >= 0);
            Assert.True(tile.X + tile.Width <= layout.CaptureWidth);
            Assert.True(tile.Y + tile.Height <= layout.CaptureHeight);
            Assert.Equal(640, tile.Width);
            Assert.Equal(640, tile.Height);
        }
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 1080)]
    [InlineData(5760, 1080)] // wide enough to need more than 2 slices
    public void SlicesAreEvenlySpreadFirstAtZeroLastFlushToTheFarEdge(int width, int height)
    {
        var layout = SliceLayout.Create(width, height, 640);
        var maxOrigin = layout.CaptureWidth - 640;

        Assert.Equal(0, layout.Slices[0].X);
        Assert.Equal(maxOrigin, layout.Slices[^1].X);
        foreach (var tile in layout.Slices) Assert.Equal(0, tile.X % 2); // even-aligned
    }

    [Fact]
    public void WideEnoughSourceProducesMoreThanTwoSlicesWithNoGapBetweenNeighbours()
    {
        // 32:9 at 640 short edge scales the long edge to ~2276px — over 3x the network size, so 2
        // slices alone would leave a blind strip down the middle. ceil(2276/640) = 4.
        var layout = SliceLayout.Create(3840, 1080, 640);

        Assert.Equal(4, layout.Slices.Count);
        for (var i = 1; i < layout.Slices.Count; i++)
        {
            // Consecutive slices must overlap or exactly abut — never leave a gap a real object could
            // sit in without appearing (even partially) in any slice.
            Assert.True(layout.Slices[i].X <= layout.Slices[i - 1].X + 640);
        }
    }

    [Fact]
    public void MapSliceBoxToSource_IdentityWholeFrameMapsBackToTheWholeCaptureExtent()
    {
        var layout = SliceLayout.Create(1920, 1080, 640);

        // Slice 0 sits at capture-pixel x=[0,640) — a box covering the whole slice should map to
        // source-normalized x=[0, 640/1138).
        var (x0, y0, x1, y1) = layout.MapSliceBoxToSource(0, 0, 0, 1, 1);
        Assert.Equal(0.0, x0, 5);
        Assert.Equal(0.0, y0, 5);
        Assert.Equal(640.0 / 1138, x1, 5);
        Assert.Equal(1.0, y1, 5);

        // Slice 1 sits at capture-pixel x=[498,1138) (the whole capture width) — a box covering the
        // whole slice should map to source-normalized x=[498/1138, 1].
        var (x0b, _, x1b, _) = layout.MapSliceBoxToSource(1, 0, 0, 1, 1);
        Assert.Equal(498.0 / 1138, x0b, 5);
        Assert.Equal(1.0, x1b, 5);
    }

    [Fact]
    public void Create_RejectsNonPositiveDimensionsAndOddNetworkSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SliceLayout.Create(0, 1080, 640));
        Assert.Throws<ArgumentOutOfRangeException>(() => SliceLayout.Create(1920, -1, 640));
        Assert.Throws<ArgumentOutOfRangeException>(() => SliceLayout.Create(1920, 1080, 641));
    }
}
