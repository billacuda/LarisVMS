namespace LarisVMS.Vision.Detection;

/// <summary>
/// Ranks one processed frame as a candidate for a camera's eager snapshot crop by how well it
/// captures <em>every</em> currently-moving object at once, not just the single best one.
///
/// The problem this solves: <see cref="CameraDetectionPipeline"/> crops the union of every moving
/// box in a frame, but it only used a per-label, single-box <see cref="MovementClassifier.Score"/>
/// to decide <em>which</em> frame's crop to keep — so the staged crop drifted toward whichever
/// frame the one dominant object peaked in, typically a frame where the other objects had already
/// left or weren't yet classified Moving. A snapshot with two tags then framed only one of them.
///
/// Ranking rule (see <see cref="CompareTo"/> / <see cref="BeatsRetained"/>): the frame that
/// captured the most distinct moving objects wins outright; ties break on the cumulative
/// <c>Σ confidence × normalizedArea</c> across those objects, so among equally-populated frames the
/// one where the objects are biggest and most confident wins.
///
/// A plain value type with no state of its own — <see cref="CameraDetectionPipeline"/> holds the
/// single "currently staged" score and compares each new frame against it, the same
/// single-inference-loop ownership model the other pure helpers here use.
/// </summary>
public readonly record struct CompositeFrameScore(int MovingCount, double CumulativeScore)
    : IComparable<CompositeFrameScore>
{
    /// <summary>More moving objects always ranks higher; equal counts fall back to the higher
    /// cumulative <c>confidence × area</c>.</summary>
    public int CompareTo(CompositeFrameScore other)
    {
        var byCount = MovingCount.CompareTo(other.MovingCount);
        return byCount != 0 ? byCount : CumulativeScore.CompareTo(other.CumulativeScore);
    }

    public static bool operator <(CompositeFrameScore left, CompositeFrameScore right) => left.CompareTo(right) < 0;
    public static bool operator >(CompositeFrameScore left, CompositeFrameScore right) => left.CompareTo(right) > 0;
    public static bool operator <=(CompositeFrameScore left, CompositeFrameScore right) => left.CompareTo(right) <= 0;
    public static bool operator >=(CompositeFrameScore left, CompositeFrameScore right) => left.CompareTo(right) >= 0;

    /// <summary>True when this frame should replace <paramref name="retained"/> as the staged crop:
    /// it captures more moving objects, or the same number but with a cumulative score clearly
    /// (<paramref name="relativeMargin"/>) above the retained one. The margin is relative because
    /// <see cref="MovementClassifier.Score"/> has no fixed scale and jitters frame to frame even
    /// when nothing meaningful changed — without it the crop would re-upload almost every frame
    /// while an object cruises steadily through the scene.</summary>
    public bool BeatsRetained(CompositeFrameScore retained, double relativeMargin) =>
        MovingCount > retained.MovingCount ||
        (MovingCount == retained.MovingCount &&
         CumulativeScore > retained.CumulativeScore * (1 + relativeMargin));

    /// <summary>Builds the score for one frame from its moving detections' <c>(confidence,
    /// normalizedArea)</c> pairs — count plus <c>Σ <see cref="MovementClassifier.Score"/></c>, the
    /// same per-box formula the rest of the pipeline ranks with.</summary>
    public static CompositeFrameScore ForFrame(IEnumerable<(double Confidence, double NormalizedArea)> movingBoxes)
    {
        var count = 0;
        var cumulative = 0.0;
        foreach (var (confidence, normalizedArea) in movingBoxes)
        {
            count++;
            cumulative += MovementClassifier.Score(confidence, normalizedArea);
        }
        return new CompositeFrameScore(count, cumulative);
    }
}
