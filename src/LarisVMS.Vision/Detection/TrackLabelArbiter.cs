namespace LarisVMS.Vision.Detection;

/// <summary>
/// Resolves D-FINE's flickering per-frame label into one stable label per ByteTrack track id, so a
/// single physical object doesn't fragment into several independent hysteresis spans/snapshots just
/// because the classifier's per-frame guess changes (a cat crossing frame read as cat -> dog -> cow
/// -> horse). ByteTrack's own association (IoU + Kalman) already keeps one stable track id through
/// the flicker — this class only decides which label that track id gets to report as.
///
/// Per track, keeps the peak confidence seen for every label it has ever been observed under, and
/// resolves to whichever label currently has the highest peak. A challenger must beat the
/// incumbent's peak by <see cref="SwitchMargin"/> to take over — enough that frame-to-frame flicker
/// can't flip the winner, but a genuinely mis-started track can still correct itself once real
/// evidence for the right label accumulates.
///
/// Not thread-safe — same single-inference-loop ownership model MovementClassifier/ByteTracker/
/// LabelBestFrameTracker already use.
/// </summary>
public sealed class TrackLabelArbiter
{
    public const double SwitchMargin = 0.10;

    private sealed class TrackState
    {
        public string WinningLabel = "";
        public readonly Dictionary<string, double> PeakConfidenceByLabel = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<int, TrackState> _tracks = new();

    /// <summary>Records one frame's observation for a track and returns the track's current winning
    /// label — not necessarily <paramref name="label"/> itself.</summary>
    public string Resolve(int trackId, string label, double confidence)
    {
        if (!_tracks.TryGetValue(trackId, out var state))
        {
            state = new TrackState { WinningLabel = label };
            state.PeakConfidenceByLabel[label] = confidence;
            _tracks[trackId] = state;
            return label;
        }

        var peak = state.PeakConfidenceByLabel.GetValueOrDefault(label, double.MinValue);
        if (confidence > peak) state.PeakConfidenceByLabel[label] = confidence;

        if (!label.Equals(state.WinningLabel, StringComparison.OrdinalIgnoreCase))
        {
            var currentPeak = state.PeakConfidenceByLabel[label];
            var winningPeak = state.PeakConfidenceByLabel.GetValueOrDefault(state.WinningLabel, double.MinValue);
            if (currentPeak > winningPeak + SwitchMargin) state.WinningLabel = label;
        }

        return state.WinningLabel;
    }

    /// <summary>Drops state for every track not in <paramref name="activeTrackIds"/> — call once per
    /// frame with ByteTracker's own currently-active set, mirroring MovementClassifier.Prune.</summary>
    public void Prune(IReadOnlySet<int> activeTrackIds)
    {
        if (_tracks.Count == 0) return;

        List<int>? toRemove = null;
        foreach (var trackId in _tracks.Keys)
        {
            if (!activeTrackIds.Contains(trackId)) (toRemove ??= []).Add(trackId);
        }

        if (toRemove is null) return;
        foreach (var trackId in toRemove) _tracks.Remove(trackId);
    }
}
