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
    public void DifferentLabelsStillMergeOnHighIoU_KeepingTheHigherConfidenceLabel()
    {
        // Confirmed in production: the same physical vehicle straddling a slice's overlap zone is
        // sometimes classified "car" by one slice and "truck" by the other, producing two nearly
        // identical stacked boxes instead of one. Label agreement is not required to merge — only
        // geometry — and the merged result keeps whichever side had the higher confidence.
        var car = new SliceMerge.Candidate(new SKRectI(100, 100, 200, 300), 0.9, "car", TouchesSliceEdge: true);
        var truck = new SliceMerge.Candidate(new SKRectI(102, 100, 202, 300), 0.6, "truck", TouchesSliceEdge: true);

        var merged = SliceMerge.Merge([car, truck]);

        var result = Assert.Single(merged);
        Assert.Equal("car", result.Label);
        Assert.Equal(0.9, result.Confidence);
    }

    [Fact]
    public void DifferentLabelsMergeViaTheSeamRuleTooWhenBothTouchAnEdge()
    {
        // Same geometry as CarWiderThanTheOverlap_MergesViaTheSeamRuleDespiteLowIoU, but the two
        // slices disagreed on the vehicle's class as well as clipping it — both problems reported
        // together in production for the same camera. The seam rule must still reunite the fragments
        // and keep the higher-confidence label for the merged box.
        var fromTile0 = new SliceMerge.Candidate(new SKRectI(490, 100, 640, 300), 0.72, "truck", TouchesSliceEdge: true);
        var fromTile1 = new SliceMerge.Candidate(new SKRectI(540, 100, 790, 300), 0.68, "car", TouchesSliceEdge: true);

        var merged = SliceMerge.Merge([fromTile0, fromTile1]);

        var result = Assert.Single(merged);
        Assert.Equal(new SKRectI(490, 100, 790, 300), result.Box);
        Assert.Equal("truck", result.Label);
        Assert.Equal(0.72, result.Confidence);
    }

    [Fact]
    public void DifferentCategoriesNeverMergeEvenOnFullOverlapAndBothEdgeFlagged()
    {
        // The direct regression test: a person and a car resolve to different CocoCategoryMap
        // categories (Human vs Vehicle), so they must never merge regardless of geometry — even the
        // worst case (identical boxes, both marked as touching a slice edge) that would otherwise
        // satisfy both the IoU and seam rules. This is what actually broke in production once
        // cross-label merging became blanket-permissive: an unrelated object standing at a slice's
        // true frame border (falsely flagged as edge-touching — see CameraDetectionPipeline's own
        // TouchesSliceEdge fix) could be absorbed into a neighboring object and simply disappear.
        var person = new SliceMerge.Candidate(new SKRectI(100, 100, 200, 300), 0.9, "person", TouchesSliceEdge: true);
        var car = new SliceMerge.Candidate(new SKRectI(100, 100, 200, 300), 0.8, "car", TouchesSliceEdge: true);

        var merged = SliceMerge.Merge([person, car]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ObjectCategoryCatchAllRequiresExactLabelMatch()
    {
        // "backpack" and "chair" both fall through CocoCategoryMap's catch-all "Object" bucket, which
        // is not a real semantic group (see CocoCategoryMap's and SliceMerge's own doc comments) — two
        // different Object-bucket labels must not merge just because both landed in the catch-all,
        // even on full overlap with both edge-flagged.
        var backpack = new SliceMerge.Candidate(new SKRectI(100, 100, 200, 300), 0.9, "backpack", TouchesSliceEdge: true);
        var chair = new SliceMerge.Candidate(new SKRectI(100, 100, 200, 300), 0.8, "chair", TouchesSliceEdge: true);

        var merged = SliceMerge.Merge([backpack, chair]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void ObjectCategoryCatchAllStillMergesOnExactLabelMatch()
    {
        // The same catch-all bucket, but the same exact label — must still merge like any other
        // duplicate sighting (this is the pre-cross-label-merging behavior, preserved for Object).
        var a = new SliceMerge.Candidate(new SKRectI(100, 100, 200, 300), 0.9, "backpack", TouchesSliceEdge: true);
        var b = new SliceMerge.Candidate(new SKRectI(102, 100, 202, 300), 0.6, "backpack", TouchesSliceEdge: true);

        var merged = SliceMerge.Merge([a, b]);

        Assert.Single(merged);
    }

    [Fact]
    public void OneSidedlyClippedFragmentMergesIntoTheWholeBoxSeenByTheNeighboringSlice()
    {
        // The pre-existing half-box case (not introduced by cross-label merging): one slice clips the
        // object (edge-flagged), but the neighboring slice reports it whole, comfortably inside its
        // own overlap band (not touching that slice's own edge, so not edge-flagged). The two-sided
        // seam rule can't fire (only one side is flagged), and plain IoU under-scores a small fragment
        // against a much bigger whole box — so without the one-sided rule this fragment would survive
        // as a stray extra box next to the correct one.
        var wholeBox = new SliceMerge.Candidate(new SKRectI(500, 100, 800, 300), 0.85, "truck", TouchesSliceEdge: false);
        var clippedFragment = new SliceMerge.Candidate(new SKRectI(500, 100, 640, 300), 0.55, "car", TouchesSliceEdge: true);

        // Confirm the premise: this fragment is almost entirely contained in the whole box (the
        // one-sided rule's own high bar) but the two share low mutual IoU (small fragment vs. big box).
        Assert.True(Nms.IoU(wholeBox.Box, clippedFragment.Box) < 0.5);

        var merged = SliceMerge.Merge([wholeBox, clippedFragment], out var stats);

        var result = Assert.Single(merged);
        Assert.Equal(wholeBox.Box, result.Box); // the fragment is already fully inside the whole box
        Assert.Equal("truck", result.Label); // higher confidence wins
        Assert.Equal(1, stats.OneSidedSeamAbsorbed);
        Assert.Equal(0, stats.SeamAbsorbed);
    }

    [Fact]
    public void OneSidedSeamRuleRequiresNearlyFullContainmentNotJustPartialOverlap()
    {
        // The one-sided rule's threshold is deliberately much stricter than the two-sided seam rule's
        // — a fragment merely overlapping a neighboring object by a modest amount (not almost entirely
        // contained) must not merge, or two genuinely separate objects near a seam could wrongly merge
        // with only one edge-flag to go on.
        var wholeBox = new SliceMerge.Candidate(new SKRectI(500, 100, 800, 300), 0.85, "truck", TouchesSliceEdge: false);
        var partiallyOverlapping = new SliceMerge.Candidate(new SKRectI(750, 100, 900, 300), 0.55, "car", TouchesSliceEdge: true);

        var merged = SliceMerge.Merge([wholeBox, partiallyOverlapping]);

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
