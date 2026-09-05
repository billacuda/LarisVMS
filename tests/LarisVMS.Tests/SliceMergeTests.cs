using SkiaSharp;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

public class SliceMergeTests
{
    [Fact]
    public void ObjectWhollyInsideTheOverlap_MergesOnPlainIoUAlone()
    {
        // Two near-identical sightings of a small object well inside the overlap between slices —
        // the easy case a bare IoU threshold already handles correctly. Neither is marked as
        // touching a slice edge, so this also proves the seam rule isn't required for this case.
        var candidates = new[]
        {
            new SliceMerge.Candidate(new SKRectI(100, 100, 160, 150), 0.90, "person", TouchesSliceEdge: false),
            new SliceMerge.Candidate(new SKRectI(104, 102, 164, 152), 0.75, "person", TouchesSliceEdge: false),
        };

        var merged = SliceMerge.Merge(candidates);

        var result = Assert.Single(merged);
        Assert.Equal(0.90, result.Confidence);
        Assert.Equal(new SKRectI(100, 100, 164, 152), result.Box); // union
    }

    [Fact]
    public void CarWiderThanTheOverlap_MergesViaTheSeamRuleDespiteLowIoU()
    {
        // The case this class exists for (see its own doc comment): a 300px-wide car straddling the
        // seam between two slices that overlap by only 100px. Tile 0 covers global x [0,640), tile 1
        // covers global x [540,1180) — the car's true extent is global x [490,790].
        //
        // In tile 0 the model only ever sees up to its own right edge (local x=640), so it reports
        // the car clipped there: global box (490,100)-(640,300), touching tile 0's edge.
        // In tile 1 the model only sees from its own left edge (local x=0) onward, reporting the car
        // clipped there too: global box (540,100)-(790,300), touching tile 1's edge.
        var fromTile0 = new SliceMerge.Candidate(new SKRectI(490, 100, 640, 300), 0.72, "car", TouchesSliceEdge: true);
        var fromTile1 = new SliceMerge.Candidate(new SKRectI(540, 100, 790, 300), 0.68, "car", TouchesSliceEdge: true);

        // Confirm the premise: plain IoU alone is well below the default 0.5 threshold, so this
        // genuinely exercises the seam rule, not the ordinary duplicate-detection path.
        Assert.True(Nms.IoU(fromTile0.Box, fromTile1.Box) < 0.5);

        var merged = SliceMerge.Merge([fromTile0, fromTile1]);

        var result = Assert.Single(merged);
        Assert.Equal(new SKRectI(490, 100, 790, 300), result.Box); // the car's true full extent
        Assert.Equal(0.72, result.Confidence); // the higher of the two
    }

    [Fact]
    public void SeamRuleNeverFiresUnlessBothCandidatesToucheAnEdge()
    {
        // Same low-IoU geometry as the seam case above, but neither candidate is marked as touching a
        // slice edge (e.g. two genuinely separate, coincidentally adjacent objects) — must not merge.
        var a = new SliceMerge.Candidate(new SKRectI(490, 100, 640, 300), 0.72, "car", TouchesSliceEdge: false);
        var b = new SliceMerge.Candidate(new SKRectI(540, 100, 790, 300), 0.68, "car", TouchesSliceEdge: false);

        var merged = SliceMerge.Merge([a, b]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void DifferentLabelsNeverMergeRegardlessOfOverlap()
    {
        var person = new SliceMerge.Candidate(new SKRectI(100, 100, 200, 300), 0.9, "person", TouchesSliceEdge: true);
        var car = new SliceMerge.Candidate(new SKRectI(100, 100, 200, 300), 0.8, "car", TouchesSliceEdge: true);

        var merged = SliceMerge.Merge([person, car]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ThreeOverlappingSightingsOfOneObjectAllCollapseIntoOne()
    {
        // A chain of three mutually-overlapping sightings (a-b and b-c both clear the default
        // threshold; a-c overlap less but are still swept up once the anchor has grown to include
        // b) must all collapse into one box, not stop at two — the reason the merge loop restarts
        // its scan after every absorption instead of a single backward sweep.
        var a = new SliceMerge.Candidate(new SKRectI(0, 0, 100, 100), 0.5, "dog", TouchesSliceEdge: false);
        var b = new SliceMerge.Candidate(new SKRectI(20, 0, 120, 100), 0.9, "dog", TouchesSliceEdge: false);
        var c = new SliceMerge.Candidate(new SKRectI(40, 0, 140, 100), 0.6, "dog", TouchesSliceEdge: false);

        var merged = SliceMerge.Merge([a, b, c]); // default 0.5 IoU threshold

        var result = Assert.Single(merged);
        Assert.Equal(new SKRectI(0, 0, 140, 100), result.Box);
        Assert.Equal(0.9, result.Confidence);
    }

    [Fact]
    public void EmptyInputProducesEmptyOutput()
    {
        Assert.Empty(SliceMerge.Merge([]));
    }
}
