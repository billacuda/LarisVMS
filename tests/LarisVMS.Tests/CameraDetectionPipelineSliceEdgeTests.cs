using LarisVMS.Vision.Inference;
using LarisVMS.Vision.Service;
using SkiaSharp;

namespace LarisVMS.Tests;

/// <summary>
/// Regression tests for <see cref="CameraDetectionPipeline.TouchesSliceEdge"/> (made <c>internal</c>
/// for this test via the project's existing <c>InternalsVisibleTo</c>) — confirmed root cause of a
/// production regression: before this fix, the test had no notion of slice index, so the first
/// slice's leading edge and the last slice's trailing edge (the camera's own true frame border, not a
/// seam) were flagged as "seam-clipped" just like a real fragment, letting <see cref="SliceMerge"/>'s
/// label-agnostic seam rule absorb unrelated neighboring objects standing at the image edge.
/// </summary>
public class CameraDetectionPipelineSliceEdgeTests
{
    [Fact]
    public void FirstSliceLeadingEdgeNeverCountsAsASeam()
    {
        // 1920x1080 -> 2 landscape slices (see SliceLayoutTests). Slice 0's leading edge (Left) is the
        // camera's own true frame border — it has no neighboring slice on that side.
        var layout = SliceLayout.Create(1920, 1080, 640);
        var boxAtLeftEdge = new SKRectI(0, 100, 100, 200);

        Assert.False(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 0, boxAtLeftEdge));
    }

    [Fact]
    public void FirstSliceTrailingEdgeStillCountsAsASeam()
    {
        // Slice 0's trailing edge (Right) borders slice 1 — a real seam.
        var layout = SliceLayout.Create(1920, 1080, 640);
        var boxAtRightEdge = new SKRectI(500, 100, 640, 200);

        Assert.True(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 0, boxAtRightEdge));
    }

    [Fact]
    public void LastSliceTrailingEdgeNeverCountsAsASeam()
    {
        // Slice 1 (the last slice) trailing edge (Right) is the camera's own true frame border.
        var layout = SliceLayout.Create(1920, 1080, 640);
        var boxAtRightEdge = new SKRectI(500, 100, 640, 200);

        Assert.False(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 1, boxAtRightEdge));
    }

    [Fact]
    public void LastSliceLeadingEdgeStillCountsAsASeam()
    {
        // Slice 1's leading edge (Left) borders slice 0 — a real seam.
        var layout = SliceLayout.Create(1920, 1080, 640);
        var boxAtLeftEdge = new SKRectI(0, 100, 100, 200);

        Assert.True(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 1, boxAtLeftEdge));
    }

    [Fact]
    public void MiddleSliceCountsBothEdgesAsSeams()
    {
        // 3840x1080 -> 4 landscape slices (a middle slice has a real neighbor on both sides).
        var layout = SliceLayout.Create(3840, 1080, 640);
        Assert.True(layout.Slices.Count >= 3, "This test needs a 3+ slice layout to exercise a middle slice.");

        var boxAtLeftEdge = new SKRectI(0, 100, 100, 200);
        var boxAtRightEdge = new SKRectI(500, 100, 640, 200);

        Assert.True(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 1, boxAtLeftEdge));
        Assert.True(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 1, boxAtRightEdge));
    }

    [Fact]
    public void BoxWellInsideATileNeverCountsAsASeamRegardlessOfIndex()
    {
        var layout = SliceLayout.Create(1920, 1080, 640);
        var boxInTheMiddle = new SKRectI(200, 200, 400, 400);

        Assert.False(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 0, boxInTheMiddle));
        Assert.False(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 1, boxInTheMiddle));
    }

    [Fact]
    public void PortraitSourceChecksTopAndBottomInsteadOfLeftAndRight()
    {
        var layout = SliceLayout.Create(1080, 1920, 640);
        var boxAtTopEdge = new SKRectI(100, 0, 200, 100);
        var boxAtBottomEdge = new SKRectI(100, 500, 200, 640);

        Assert.False(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 0, boxAtTopEdge));
        Assert.True(CameraDetectionPipeline.TouchesSliceEdge(layout, sliceIndex: 0, boxAtBottomEdge));
    }
}
