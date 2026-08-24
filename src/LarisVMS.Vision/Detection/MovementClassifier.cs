using SkiaSharp;

namespace LarisVMS.Vision.Detection;

public enum MovementState { Moving, Idle }

/// <summary>Snapshot of one track's classified state at the moment it was last observed — what a
/// caller needs to decide whether/how to report this track for this tick.</summary>
public readonly record struct TrackObservation(int TrackId, MovementState State);

/// <summary>The single best-scoring frame seen for a track so far (decision 10) — box is
/// normalized 0-1 against the frame it was observed at (the Sub stream's own decode resolution;
/// see the detection plan's Main/Sub field-of-view assumption for how this maps onto a recorded
/// frame later).</summary>
public readonly record struct BestFrame(DateTime AtUtc, double X, double Y, double W, double H, double Confidence);

public sealed class MovementClassifierOptions
{
    /// <summary>How far back the centroid history window looks when deciding Moving vs Idle.</summary>
    public TimeSpan MovementWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A track counts as Moving once its centroid has displaced more than this fraction
    /// of its own bounding-box diagonal within <see cref="MovementWindow"/> — expressed as a
    /// fraction rather than a fixed pixel distance so it scales sensibly for both a distant car
    /// (small box, small pixel displacement for real movement) and a close-up person (large box,
    /// large pixel displacement for the same relative movement).</summary>
    public double MovementThresholdFraction { get; init; } = 0.5;

    /// <summary>Upper bound on how many centroid samples one track's history keeps — a safety
    /// bound against a pathologically high frame rate growing the ring buffer unbounded, not
    /// something a normal detection frame rate would ever reach.</summary>
    public int MaxHistorySamples { get; init; } = 256;
}

/// <summary>
/// Per-track state combining two independent concerns the object detection plan groups together
/// (decisions 7 and 10) because both need the same per-track observation history:
///
/// 1. Moving vs. Idle classification (decision 7) — ByteTrack already gives per-object identity
///    continuity across frames; LarisVMS.Media.MotionDetector-style pixel frame-diffing is a
///    different, track-unaware signal and isn't used here. A track is "Moving" once its centroid
///    has displaced more than a threshold (relative to its own box size) within a rolling window.
///
/// 2. Best-frame tracking for snapshot cropping (decision 10) — the single frame across a track's
///    whole lifetime with the best combination of box size and confidence
///    (score = confidence * normalizedBoxArea), so a later snapshot crop is drawn from the
///    clearest/most confident sighting rather than simply the first one.
///
/// Not thread-safe — intended to be owned by exactly one camera's inference loop, the same
/// ownership model <see cref="Inference.YoloEngine"/> and <see cref="Tracking.ByteTracker"/>
/// already use.
/// </summary>
public sealed class MovementClassifier(MovementClassifierOptions? options = null)
{
    private readonly MovementClassifierOptions _options = options ?? new MovementClassifierOptions();
    private readonly Dictionary<int, TrackHistory> _tracks = [];

    private sealed class TrackHistory
    {
        public readonly List<(DateTime AtUtc, SKPoint Centroid)> Samples = [];
        public BestFrame? Best;
        public double BestScore = double.MinValue;
    }

    /// <summary>
    /// Records one track's observation for this frame and returns its current classification.
    /// <paramref name="boundingBox"/> is in the same pixel coordinate space as
    /// <paramref name="frameWidth"/>/<paramref name="frameHeight"/> (the decoded frame's own
    /// resolution) — normalized internally for both the movement threshold and the stored best
    /// frame's box.
    /// </summary>
    public TrackObservation Observe(int trackId, SKRectI boundingBox, double confidence,
        int frameWidth, int frameHeight, DateTime nowUtc)
    {
        if (!_tracks.TryGetValue(trackId, out var history))
        {
            history = new TrackHistory();
            _tracks[trackId] = history;
        }

        var centroid = new SKPoint(
            boundingBox.Left + (boundingBox.Width / 2f),
            boundingBox.Top + (boundingBox.Height / 2f));

        history.Samples.Add((nowUtc, centroid));

        // Drop samples outside the movement window — and as a safety bound, never let the buffer
        // grow past MaxHistorySamples regardless of timestamps (a pathologically high frame rate
        // could otherwise accumulate faster than the time-based trim below removes).
        var cutoff = nowUtc - _options.MovementWindow;
        history.Samples.RemoveAll(s => s.AtUtc < cutoff);
        if (history.Samples.Count > _options.MaxHistorySamples)
        {
            history.Samples.RemoveRange(0, history.Samples.Count - _options.MaxHistorySamples);
        }

        var state = ClassifyMovement(history, boundingBox);

        UpdateBestFrame(history, boundingBox, confidence, frameWidth, frameHeight, nowUtc);

        return new TrackObservation(trackId, state);
    }

    /// <summary>Proposed default scoring for decision 10's "clearest/largest frame" selection —
    /// pure and independently testable so it's cheap to retune (see the plan's open questions).
    /// Rewards a box that's both confident and large; the product means doubling either factor
    /// doubles the score, rather than one dominating the other. Public (not just used internally)
    /// because LarisVMS.Vision.Service's own per-label (not just per-track) best-frame tracking
    /// reuses the identical formula — see CameraDetectionPipeline.</summary>
    public static double Score(double confidence, double normalizedBoxArea) => confidence * normalizedBoxArea;

    private MovementState ClassifyMovement(TrackHistory history, SKRectI currentBox)
    {
        if (history.Samples.Count < 2) return MovementState.Idle;

        var oldest = history.Samples[0].Centroid;
        var newest = history.Samples[^1].Centroid;
        var displacement = MathF.Sqrt(MathF.Pow(newest.X - oldest.X, 2) + MathF.Pow(newest.Y - oldest.Y, 2));

        var diagonal = MathF.Sqrt((float)(currentBox.Width * currentBox.Width + currentBox.Height * currentBox.Height));
        if (diagonal <= 0f) return MovementState.Idle;

        return displacement / diagonal >= _options.MovementThresholdFraction ? MovementState.Moving : MovementState.Idle;
    }

    private static void UpdateBestFrame(TrackHistory history, SKRectI box, double confidence,
        int frameWidth, int frameHeight, DateTime nowUtc)
    {
        if (frameWidth <= 0 || frameHeight <= 0) return;

        var x = box.Left / (double)frameWidth;
        var y = box.Top / (double)frameHeight;
        var w = box.Width / (double)frameWidth;
        var h = box.Height / (double)frameHeight;

        var normalizedArea = Math.Clamp(w * h, 0.0, 1.0);
        var score = Score(confidence, normalizedArea);

        if (score > history.BestScore)
        {
            history.BestScore = score;
            history.Best = new BestFrame(nowUtc, x, y, w, h, confidence);
        }
    }

    /// <summary>The best frame recorded so far for a track, or null if the track has never been
    /// observed (or was pruned). Read at span-checkpoint/close time — see the detection plan's
    /// decision 10 for how this becomes MotionSpanReportItem.BestFrameAtUtc/BestBoxX/Y/W/H.</summary>
    public BestFrame? GetBestFrame(int trackId) => _tracks.TryGetValue(trackId, out var h) ? h.Best : null;

    /// <summary>Drops state for every track not in <paramref name="activeTrackIds"/> — called once
    /// per frame with whatever <see cref="Tracking.ByteTracker.Update{T}"/> just reported as still
    /// active, so a track ByteTrack itself considers gone (fully removed, not merely occluded —
    /// ByteTrack's own second association pass already keeps a briefly-occluded track's ID alive)
    /// doesn't leak memory here indefinitely.</summary>
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
