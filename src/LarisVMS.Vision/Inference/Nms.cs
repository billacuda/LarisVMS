using SkiaSharp;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 3b: standard greedy non-maximum suppression.
///
/// Confirmed nothing like this exists anywhere else in LarisVMS.Vision — D-FINE's own DETR-style
/// output (one query per object) never produces duplicate boxes *within* a single image, which is
/// exactly why <see cref="DFineEngine"/>/<see cref="DFineDecoder"/>'s own `iou` parameters are
/// documented as inert. That stops being true the moment two *separate* passes can see the same
/// physical object — pass 3b's whole-frame letterboxed pass and its native-scale tiles are exactly
/// that: a large/near object typically appears whole in the whole-frame pass AND as one or more
/// clipped fragments in whichever tiles it happens to overlap. This class collapses those into one
/// box, keeping whichever candidate scored highest — per the plan's own reasoning, a whole-frame box
/// beats its own tile fragments on confidence (a clipped door/wheel/fragment reads as a worse view of
/// the same object than the one that saw all of it).
/// </summary>
public static class Nms
{
    /// <summary>Greedily keeps the highest-confidence candidate, discards every other candidate whose
    /// IoU against it is at or above <paramref name="iouThreshold"/>, then repeats with whatever's
    /// left — the standard greedy NMS algorithm, generic over any item shape via the two selector
    /// functions so callers don't need to project into a fixed tuple first.</summary>
    public static List<T> Suppress<T>(IReadOnlyList<T> items, Func<T, SKRectI> boxSelector,
        Func<T, double> confidenceSelector, double iouThreshold = 0.5)
    {
        var ordered = items.OrderByDescending(confidenceSelector).ToList();
        var kept = new List<T>();

        while (ordered.Count > 0)
        {
            var best = ordered[0];
            kept.Add(best);
            ordered.RemoveAt(0);

            var bestBox = boxSelector(best);
            ordered.RemoveAll(candidate => IoU(bestBox, boxSelector(candidate)) >= iouThreshold);
        }

        return kept;
    }

    /// <summary>Finds whichever candidate overlaps <paramref name="target"/> the most, or null if
    /// every candidate's IoU against it is 0 (no overlap at all) — used to re-associate a fresh
    /// detection result back to the specific object it was triggered by, when the same result set
    /// also contains other, unrelated objects (pass 3b's whole-frame pass reports every object it
    /// sees, not just the one that triggered it).</summary>
    public static T? FindBestMatch<T>(IReadOnlyList<T> candidates, SKRectI target, Func<T, SKRectI> boxSelector)
        where T : struct
    {
        T? best = null;
        var bestIoU = 0.0;
        foreach (var candidate in candidates)
        {
            var iou = IoU(target, boxSelector(candidate));
            if (iou > bestIoU)
            {
                bestIoU = iou;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>Intersection-over-union of two rectangles, 0 when they don't overlap at all.</summary>
    internal static double IoU(SKRectI a, SKRectI b)
    {
        var left = Math.Max(a.Left, b.Left);
        var top = Math.Max(a.Top, b.Top);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);

        var intersectionWidth = right - left;
        var intersectionHeight = bottom - top;
        if (intersectionWidth <= 0 || intersectionHeight <= 0) return 0.0;

        var intersectionArea = (double)intersectionWidth * intersectionHeight;
        var areaA = (double)a.Width * a.Height;
        var areaB = (double)b.Width * b.Height;
        var unionArea = areaA + areaB - intersectionArea;
        return unionArea <= 0 ? 0.0 : intersectionArea / unionArea;
    }
}
