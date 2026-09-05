using SkiaSharp;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 4: merges per-slice detections (each already
/// mapped into global source-pixel space by <see cref="SliceLayout.MapSliceBoxToSource"/>) into one
/// list, collapsing the same physical object seen in more than one overlapping slice.
///
/// Deliberately a **union** merge, not <see cref="Nms"/>'s own suppression: <see cref="Nms.Suppress"/>
/// *discards* the lower-confidence duplicate outright, which is correct when both candidates saw the
/// whole object (the usual NMS case) but wrong here — an object straddling a slice boundary is
/// genuinely clipped in each slice that only partly contains it, so discarding one candidate keeps a
/// truncated box instead of the two fragments' true combined extent. This class keeps the **union**
/// of every merged box's extent and the highest confidence among them.
///
/// <b>Why a plain IoU threshold isn't enough on its own</b> — the reason this is its own class
/// rather than a call to <see cref="Nms.Suppress"/> with a different combine rule: an object small
/// enough to sit wholly inside a slice overlap produces two near-identical boxes (high IoU, the easy
/// case a threshold alone handles). An object *wider* than the overlap is clipped differently in
/// each slice — a car's left half in one slice, right half in the other — so the two fragments have
/// **low** mutual IoU (their union is much bigger than either fragment, and IoU's denominator grows
/// with the union) even though they are unambiguously the same object. A single global IoU threshold
/// either merges the easy case correctly and misses this one, or is loosened enough to catch this one
/// and starts merging genuinely separate nearby objects. The fix: also merge when both candidates
/// came from a slice edge along the direction slices overlap (<see cref="Candidate.TouchesSliceEdge"/>)
/// **and** their overlap is large relative to the smaller fragment (intersection-over-smaller, not
/// IoU — exactly the ratio that stays high for two clipped fragments of one big object regardless of
/// how big their union ends up being).
/// </summary>
public static class SliceMerge
{
    /// <summary>One slice's own detection, already mapped into global source-pixel space.
    /// <paramref name="TouchesSliceEdge"/> is computed by the caller from the box's *pre-mapping*,
    /// slice-local coordinates — true when the box touched (within a small pixel tolerance) that
    /// slice's own edge along the axis slices overlap on, i.e. the model's own view of this object was
    /// itself cut off by the tile boundary, not just close to the image edge for some other reason.</summary>
    public readonly record struct Candidate(SKRectI Box, double Confidence, string Label, bool TouchesSliceEdge);

    /// <summary>Which of <see cref="Qualifies"/>' two rules actually absorbed each candidate, over one
    /// <see cref="Merge"/> call. The distinction is the whole diagnostic value: <see cref="IouAbsorbed"/>
    /// counts the easy case (an object small enough to sit wholly inside the overlap, seen twice),
    /// <see cref="SeamAbsorbed"/> counts the case this class was written for (one object clipped
    /// differently in two slices). Seam absorptions sitting at zero while both slices are producing
    /// detections is the signature of a merge that isn't working; per-slice detections themselves
    /// falling off near the seam points at the geometry or the model instead.</summary>
    public readonly record struct MergeStats(int IouAbsorbed, int SeamAbsorbed);

    /// <summary>Merges without reporting <see cref="MergeStats"/> — the overload below does the work
    /// and documents the rules. Kept so callers that only want the result (and every existing test)
    /// read the same as they always did.</summary>
    public static List<Candidate> Merge(IReadOnlyList<Candidate> candidates,
        double iouThreshold = 0.5, double seamOverlapThreshold = 0.3) =>
        Merge(candidates, out _, iouThreshold, seamOverlapThreshold);

    /// <summary>Greedily merges <paramref name="candidates"/>: repeatedly takes the highest-confidence
    /// remaining candidate as a group's anchor, then absorbs every same-label candidate that either
    /// clears <paramref name="iouThreshold"/> against it (the ordinary duplicate-detection case) or —
    /// when both are marked <see cref="Candidate.TouchesSliceEdge"/> — clears
    /// <paramref name="seamOverlapThreshold"/> on intersection-over-smaller (the seam-straddling case
    /// this class exists for; see its own doc comment). Absorbing a candidate grows the anchor's own
    /// box to their union, which can bring it into range of a candidate already scanned past in this
    /// same pass — so each absorption restarts the scan over what's left, rather than a single
    /// backward sweep the way <see cref="Nms.Suppress"/>'s simpler discard-only merge can get away
    /// with.</summary>
    public static List<Candidate> Merge(IReadOnlyList<Candidate> candidates, out MergeStats stats,
        double iouThreshold = 0.5, double seamOverlapThreshold = 0.3)
    {
        var pool = new List<Candidate>(candidates);
        var merged = new List<Candidate>();
        int iouAbsorbed = 0, seamAbsorbed = 0;

        while (pool.Count > 0)
        {
            var bestIndex = 0;
            for (var i = 1; i < pool.Count; i++)
            {
                if (pool[i].Confidence > pool[bestIndex].Confidence) bestIndex = i;
            }
            var current = pool[bestIndex];
            pool.RemoveAt(bestIndex);

            bool absorbedAny;
            do
            {
                absorbedAny = false;
                for (var i = pool.Count - 1; i >= 0; i--)
                {
                    var candidate = pool[i];
                    if (!string.Equals(candidate.Label, current.Label, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Qualifies(current, candidate, iouThreshold, seamOverlapThreshold) is not { } rule) continue;
                    if (rule == Rule.Iou) iouAbsorbed++; else seamAbsorbed++;

                    current = new Candidate(
                        Union(current.Box, candidate.Box),
                        Math.Max(current.Confidence, candidate.Confidence),
                        current.Label,
                        current.TouchesSliceEdge || candidate.TouchesSliceEdge);
                    pool.RemoveAt(i);
                    absorbedAny = true;
                }
            } while (absorbedAny);

            merged.Add(current);
        }

        stats = new MergeStats(iouAbsorbed, seamAbsorbed);
        return merged;
    }

    private enum Rule { Iou, Seam }

    /// <summary>Which rule lets <paramref name="b"/> be absorbed into <paramref name="a"/>, or null
    /// for neither. Returns the rule rather than a bool only so <see cref="MergeStats"/> can tell the
    /// two apart — the decision itself is unchanged, and IoU is still tried first.</summary>
    private static Rule? Qualifies(Candidate a, Candidate b, double iouThreshold, double seamOverlapThreshold)
    {
        if (Nms.IoU(a.Box, b.Box) >= iouThreshold) return Rule.Iou;
        return a.TouchesSliceEdge && b.TouchesSliceEdge
            && IntersectionOverSmaller(a.Box, b.Box) >= seamOverlapThreshold
            ? Rule.Seam
            : null;
    }

    /// <summary>Intersection area over the *smaller* of the two boxes' own areas, rather than IoU's
    /// union-area denominator. Stays high for two fragments of one object split across a slice seam
    /// regardless of how large their combined (union) extent ends up being — exactly the case IoU
    /// alone under-scores (see this class's own doc comment).</summary>
    private static double IntersectionOverSmaller(SKRectI a, SKRectI b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);

        var iw = right - left;
        var ih = bottom - top;
        if (iw <= 0 || ih <= 0) return 0.0;

        var intersectionArea = (double)iw * ih;
        var smaller = Math.Min((double)a.Width * a.Height, (double)b.Width * b.Height);
        return smaller <= 0 ? 0.0 : intersectionArea / smaller;
    }

    private static SKRectI Union(SKRectI a, SKRectI b) => new(
        Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
        Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}
