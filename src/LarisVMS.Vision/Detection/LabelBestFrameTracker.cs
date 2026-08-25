namespace LarisVMS.Vision.Detection;

/// <summary>
/// Per-label "best frame" bookkeeping for CameraDetectionPipeline's snapshot cropping (decision 10),
/// pulled out as its own pure/testable class the same way MotionHysteresis and MovementClassifier
/// are — CameraDetectionPipeline owns one instance and feeds it every motion-contributing detection.
///
/// Two tiers per label rather than one: MovementClassifier.Score's confidence*area formula rewards
/// box area heavily, so without a cap a large, well-lit, stable object (a parked truck sharing a
/// label with a smaller passing car, or a spurious oversized false-positive) can permanently
/// outscore every correctly-sized real detection. A candidate whose box covers more than
/// <see cref="OversizedAreaFraction"/> of the frame competes in its own separate pool, only used
/// as a fallback when no normal-sized candidate exists at all — so a genuine close-up (a face
/// filling the frame, nothing smaller ever seen) still wins when it's the only candidate.
///
/// Not thread-safe — same single-inference-loop ownership model MovementClassifier/ByteTracker
/// already use.
/// </summary>
public sealed class LabelBestFrameTracker
{
    public const double OversizedAreaFraction = 0.8;

    private sealed class Candidates
    {
        public BestFrame? Normal;
        public double NormalScore = double.MinValue;
        public BestFrame? Oversized;
        public double OversizedScore = double.MinValue;
    }

    private readonly Dictionary<string, Candidates> _byLabel = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records one candidate frame for a label — <paramref name="normalizedArea"/> and
    /// <paramref name="confidence"/> are combined via MovementClassifier.Score, the same shared
    /// formula MovementClassifier itself uses for per-track best-frame selection, so this class's
    /// per-label ranking always agrees with it.</summary>
    public void Observe(string label, BestFrame frame, double normalizedArea, double confidence)
    {
        var score = MovementClassifier.Score(confidence, normalizedArea);
        if (!_byLabel.TryGetValue(label, out var candidates))
        {
            candidates = new Candidates();
            _byLabel[label] = candidates;
        }

        if (normalizedArea > OversizedAreaFraction)
        {
            if (score > candidates.OversizedScore)
            {
                candidates.Oversized = frame;
                candidates.OversizedScore = score;
            }
        }
        else if (score > candidates.NormalScore)
        {
            candidates.Normal = frame;
            candidates.NormalScore = score;
        }
    }

    /// <summary>The best frame recorded for a label so far, or null if none has been observed —
    /// the normal-sized candidate when one exists, falling back to the oversized one only when
    /// nothing normal-sized was ever seen.</summary>
    public BestFrame? GetBest(string label)
        => _byLabel.TryGetValue(label, out var candidates) ? candidates.Normal ?? candidates.Oversized : null;

    /// <summary>Drops a label's candidates — call once its span has genuinely closed, so a later,
    /// separate span for the same label runs its own fresh contest instead of being handed a stale
    /// sighting (a truck that won weeks ago) that could otherwise never lose to a real detection.</summary>
    public void Reset(string label) => _byLabel.Remove(label);
}
