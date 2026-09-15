using LarisVMS.Core;
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
/// how big their union ends up being). A *one-sided* variant of the same idea
/// (<see cref="OneSidedSeamOverlapThreshold"/>) additionally covers an object clipped in only one of
/// the two slices that see it (whole in the other, so only one candidate is edge-flagged) — see
/// <see cref="Qualifies"/>'s own doc comment.
///
/// <b>Cross-label merging is gated by semantic category, not blanket-allowed.</b> A vehicle straddling
/// a seam (or sitting in the overlap zone, seen whole by both slices) is frequently classified
/// differently by each slice — "car" vs "truck" is the observed real case, since a partial or
/// slightly-cropped view of a vehicle is genuine ambiguity between visually similar classes, not a
/// decoder bug. Requiring identical labels to merge left every such pair as two separate, wrong
/// detections — either two near-duplicate boxes stacked on each other, or two half-boxes never
/// reunited into the vehicle's true extent. But merging on geometry alone regardless of label turned
/// out to be too permissive the other way: two genuinely different, coincidentally-adjacent objects
/// (a person standing near a car, both near a slice's true frame border — see
/// <see cref="Candidate.TouchesSliceEdge"/>'s own doc comment for why that flag alone isn't enough to
/// rule this out) could then wrongly merge, making one of them disappear from every downstream
/// consumer entirely (see <see cref="CameraDetectionPipeline"/>'s own reporting/hysteresis loop — an
/// absorbed candidate isn't just missing a box, its whole timeline span winds down as if it left
/// frame). The fix here: <see cref="CocoCategoryMap.Resolve"/> already groups a raw class name into
/// Human/Vehicle/Animal/Object — two candidates may merge across labels only when they resolve to the
/// same *non-Object* category (car/truck/bus are all Vehicle; a person can never merge with a car).
/// <c>Object</c> is COCO's/Objects365's catch-all for everything else (backpack, chair, laptop, ...) —
/// not a real semantic group — so two Object-category candidates still require an exact label match,
/// same as before cross-label merging existed at all. A label neither vocabulary recognizes (a
/// non-COCO/Objects365 custom model) resolves to Object on both sides too, so it falls back to the
/// same conservative exact-match behavior — no regression for models outside COCO's vocabulary. The
/// merged candidate keeps whichever original label had the higher confidence, on the theory that the
/// fuller/less-obstructed view is also the more reliable classification.
/// </summary>
public static class SliceMerge
{
    /// <summary>Intersection-over-smaller threshold for the two-sided seam rule (both candidates
    /// edge-flagged) — see <see cref="Qualifies"/>.</summary>
    public const double DefaultSeamOverlapThreshold = 0.3;

    /// <summary>Intersection-over-smaller threshold for the one-sided seam rule (only one candidate
    /// edge-flagged) — deliberately stricter than <see cref="DefaultSeamOverlapThreshold"/>: a genuine
    /// fragment of a whole object seen in the neighboring slice should be *almost entirely* contained
    /// within the whole box, not merely overlapping it. See <see cref="Qualifies"/>.</summary>
    public const double DefaultOneSidedSeamOverlapThreshold = 0.85;

    /// <summary>One slice's own detection, already mapped into global source-pixel space.
    /// <paramref name="TouchesSliceEdge"/> is computed by the caller from the box's *pre-mapping*,
    /// slice-local coordinates — true when the box touched (within a small pixel tolerance) an edge
    /// this slice actually shares with a neighboring slice, i.e. the model's own view of this object
    /// was itself cut off by a tile boundary. The caller is responsible for excluding a slice's true
    /// outer frame border (the leading edge of the first slice, the trailing edge of the last) from
    /// this flag — that edge borders nothing, so an object standing there is not a seam fragment, and
    /// flagging it as one is what let two genuinely separate objects wrongly merge in production (see
    /// this class's own doc comment).</summary>
    public readonly record struct Candidate(SKRectI Box, double Confidence, string Label, bool TouchesSliceEdge);

    /// <summary>Which of <see cref="Qualifies"/>' rules actually absorbed each candidate, over one
    /// <see cref="Merge"/> call. The distinction is the whole diagnostic value: <see cref="IouAbsorbed"/>
    /// counts the easy case (an object small enough to sit wholly inside the overlap, seen twice),
    /// <see cref="SeamAbsorbed"/> counts one object clipped differently in two slices (both sides
    /// edge-flagged), and <see cref="OneSidedSeamAbsorbed"/> counts one object clipped in only one of
    /// the two slices that saw it. Seam counts sitting at zero while both slices are producing
    /// detections is the signature of a merge that isn't working; per-slice detections themselves
    /// falling off near the seam points at the geometry or the model instead.</summary>
    public readonly record struct MergeStats(int IouAbsorbed, int SeamAbsorbed, int OneSidedSeamAbsorbed);

    /// <summary>Merges without reporting <see cref="MergeStats"/> — the overload below does the work
    /// and documents the rules. Kept so callers that only want the result (and every existing test)
    /// read the same as they always did.</summary>
    public static List<Candidate> Merge(IReadOnlyList<Candidate> candidates,
        double iouThreshold = 0.5, double seamOverlapThreshold = DefaultSeamOverlapThreshold,
        double oneSidedSeamOverlapThreshold = DefaultOneSidedSeamOverlapThreshold) =>
        Merge(candidates, out _, iouThreshold, seamOverlapThreshold, oneSidedSeamOverlapThreshold);

    /// <summary>Greedily merges <paramref name="candidates"/>: repeatedly takes the highest-confidence
    /// remaining candidate as a group's anchor, then absorbs every candidate <see cref="Qualifies"/>
    /// allows (same-category-or-label geometry match — see this class's own doc comment). Absorbing a
    /// candidate grows the anchor's own box to their union, which can bring it into range of a
    /// candidate already scanned past in this same pass — so each absorption restarts the scan over
    /// what's left, rather than a single backward sweep the way <see cref="Nms.Suppress"/>'s simpler
    /// discard-only merge can get away with.</summary>
    public static List<Candidate> Merge(IReadOnlyList<Candidate> candidates, out MergeStats stats,
        double iouThreshold = 0.5, double seamOverlapThreshold = DefaultSeamOverlapThreshold,
        double oneSidedSeamOverlapThreshold = DefaultOneSidedSeamOverlapThreshold)
    {
        var pool = new List<Candidate>(candidates);
        var merged = new List<Candidate>();
        int iouAbsorbed = 0, seamAbsorbed = 0, oneSidedSeamAbsorbed = 0;

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
                    if (Qualifies(current, candidate, iouThreshold, seamOverlapThreshold, oneSidedSeamOverlapThreshold) is not { } rule)
                        continue;
                    switch (rule)
                    {
                        case Rule.Iou: iouAbsorbed++; break;
                        case Rule.Seam: seamAbsorbed++; break;
                        default: oneSidedSeamAbsorbed++; break;
                    }

                    // Label disagreement is expected within a category (see this class's own doc
                    // comment) — keep whichever side's label came with the higher confidence rather
                    // than always the running anchor's, since the anchor is only "whichever is left
                    // with the highest confidence so far," not necessarily the higher of this pair.
                    var keepCurrentLabel = current.Confidence >= candidate.Confidence;
                    current = new Candidate(
                        Union(current.Box, candidate.Box),
                        Math.Max(current.Confidence, candidate.Confidence),
                        keepCurrentLabel ? current.Label : candidate.Label,
                        current.TouchesSliceEdge || candidate.TouchesSliceEdge);
                    pool.RemoveAt(i);
                    absorbedAny = true;
                }
            } while (absorbedAny);

            merged.Add(current);
        }

        stats = new MergeStats(iouAbsorbed, seamAbsorbed, oneSidedSeamAbsorbed);
        return merged;
    }

    private enum Rule { Iou, Seam, OneSidedSeam }

    /// <summary>Which rule lets <paramref name="b"/> be absorbed into <paramref name="a"/>, or null
    /// for neither. First gates on category — same non-<c>Object</c>
    /// <see cref="CocoCategoryMap"/> category, or an exact label match — then tries, in order:
    /// (1) plain IoU ≥ <paramref name="iouThreshold"/> (two full/near-full sightings of one object);
    /// (2) both candidates edge-flagged and intersection-over-smaller ≥
    /// <paramref name="seamOverlapThreshold"/> (one object clipped differently by two adjacent
    /// slices); (3) exactly one candidate edge-flagged and intersection-over-smaller ≥
    /// <paramref name="oneSidedSeamOverlapThreshold"/> (one object clipped by one slice but seen whole
    /// by its neighbor, so only one fragment carries the flag — plain IoU under-scores this case since
    /// the fragment is small relative to the whole box's contribution to the union). Rule 3 requires a
    /// much higher containment ratio than rule 2 specifically because it has only one edge-flag to go
    /// on, not two — a real fragment-of-a-whole should be almost entirely swallowed by the whole box,
    /// not merely overlapping it, or two separate objects near a seam could wrongly merge.</summary>
    private static Rule? Qualifies(Candidate a, Candidate b, double iouThreshold, double seamOverlapThreshold,
        double oneSidedSeamOverlapThreshold)
    {
        if (!MayMerge(a.Label, b.Label)) return null;

        if (Nms.IoU(a.Box, b.Box) >= iouThreshold) return Rule.Iou;

        var edgeFlags = (a.TouchesSliceEdge ? 1 : 0) + (b.TouchesSliceEdge ? 1 : 0);
        if (edgeFlags == 0) return null;

        var overlap = IntersectionOverSmaller(a.Box, b.Box);
        if (edgeFlags == 2) return overlap >= seamOverlapThreshold ? Rule.Seam : null;
        return overlap >= oneSidedSeamOverlapThreshold ? Rule.OneSidedSeam : null;
    }

    /// <summary>True when two raw class labels are allowed to merge across a label disagreement —
    /// same non-<c>Object</c> <see cref="CocoCategoryMap"/> category, or an exact (case-insensitive)
    /// label match. See this class's own doc comment for why <c>Object</c> (the catch-all) requires
    /// exact match rather than treating it as a real group.</summary>
    private static bool MayMerge(string labelA, string labelB)
    {
        if (string.Equals(labelA, labelB, StringComparison.OrdinalIgnoreCase)) return true;

        var categoryA = CocoCategoryMap.Resolve(labelA);
        if (categoryA == CocoCategoryMap.Object) return false;
        return categoryA == CocoCategoryMap.Resolve(labelB);
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
