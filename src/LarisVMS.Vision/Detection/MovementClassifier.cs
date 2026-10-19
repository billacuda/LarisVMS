using SkiaSharp;

namespace LarisVMS.Vision.Detection;

public enum MovementState { Moving, Idle }

/// <summary>Which rule decides Moving vs Idle (Detection.MovementMode, per camera).</summary>
public enum MovementMode
{
    /// <summary>The pre-0.218 rule: the centre must travel half the box diagonal within
    /// <see cref="MovementClassifierOptions.MovementWindow"/> (optionally with jitter rejection).</summary>
    Legacy,

    /// <summary>Decides from the shape of the track's recent path: a few consistent steps for a fast
    /// mover, a steady straight-line trend for a slow one, the unclipped edge for an object entering
    /// at the frame border or from behind something, and box growth for one coming straight at the
    /// camera — with a threshold that grows more slowly than the box, so a large slow truck isn't
    /// held to the same relative distance as a small fast car.</summary>
    Standard,
}

/// <summary>Detection.MovementSensitivity: scales <see cref="MovementMode.Standard"/>'s thresholds.</summary>
public enum MovementSensitivity { Low, Normal, High }

/// <summary>Snapshot of one track's classified state at the moment it was last observed — what a
/// caller needs to decide whether/how to report this track for this tick. <paramref name="Reason"/>
/// is set on the observation where a <see cref="MovementMode.Standard"/> track turns Moving: which rule
/// fired (for the debug log that tunes the thresholds), null otherwise.</summary>
public readonly record struct TrackObservation(int TrackId, MovementState State, string? Reason = null);

/// <summary>The single best-scoring frame seen for a track so far (decision 10) — box is
/// normalized 0-1 against the frame it was observed at (the Sub stream's own decode resolution;
/// see the detection plan's Main/Sub field-of-view assumption for how this maps onto a recorded
/// frame later).</summary>
public readonly record struct BestFrame(DateTime AtUtc, double X, double Y, double W, double H, double Confidence);

public sealed class MovementClassifierOptions
{
    /// <summary>Legacy mode only (or <see cref="Mode"/> unset, the library default): which rule
    /// classifies. The pipeline passes Detection.MovementMode, whose own default is Standard.</summary>
    public MovementMode Mode { get; init; } = MovementMode.Legacy;

    /// <summary>Standard mode only: how readily movement is called.</summary>
    public MovementSensitivity Sensitivity { get; init; } = MovementSensitivity.Normal;

    /// <summary>Legacy mode: how far back the centroid history window looks when deciding Moving vs
    /// Idle. (Standard mode keeps <see cref="StandardHistory"/> and decides from as few as 3
    /// samples, so it never waits out a window.)</summary>
    public TimeSpan MovementWindow { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Standard mode: the longest stretch of history looked at. Only a slow mover ever needs
    /// this much; a fast one is decided from its last few samples.</summary>
    public TimeSpan StandardHistory { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Legacy mode: a track counts as Moving once its centroid has displaced more than this
    /// fraction of its own bounding-box diagonal within <see cref="MovementWindow"/>.</summary>
    public double MovementThresholdFraction { get; init; } = 0.5;

    /// <summary>Upper bound on how many samples one track's history keeps — a safety bound against a
    /// pathologically high frame rate growing the buffer unbounded, not something a normal detection
    /// frame rate would ever reach.</summary>
    public int MaxHistorySamples { get; init; } = 256;

    /// <summary>Legacy mode only. When true, Moving/Idle is decided from the net displacement between
    /// <em>lightly smoothed</em> (up to two samples each) endpoints, which must clear both an absolute
    /// pixel floor (<see cref="MinNetDisplacementPixels"/>) and <see cref="MovementThresholdFraction"/>
    /// of the box diagonal. When false, the original single oldest-vs-newest centroid comparison.
    /// This is the per-camera Detection.RejectMotionJitter toggle.</summary>
    public bool JitterRejectionEnabled { get; init; }

    /// <summary>The smallest travel, in pixels, that can ever count as movement, whatever the box
    /// size (Detection.MotionJitterPixels, 1-15). Legacy mode applies it only with jitter rejection
    /// on; Standard mode always does.</summary>
    public double MinNetDisplacementPixels { get; init; } = 3.0;
}

/// <summary>
/// Per-track state combining two independent concerns the object detection plan groups together
/// (decisions 7 and 10) because both need the same per-track observation history:
///
/// 1. Moving vs. Idle classification (decision 7) — ByteTrack already gives per-object identity
///    continuity across frames; this decides from each track's own box history whether the object
///    is actually going somewhere. See <see cref="MovementMode"/> for the two rules.
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
    private List<(int TrackId, SKRectI Box)> _frameBoxes = [];

    // The low four bits: an edge that can't be trusted (cut off by the frame border, or inside another
    // object's box). The same bit shifted up four: cut off by the frame border specifically.
    [Flags]
    private enum Edges
    {
        None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8,
        LeftAtBorder = 16, TopAtBorder = 32, RightAtBorder = 64, BottomAtBorder = 128,
    }

    private readonly record struct Sample(DateTime AtUtc, float L, float T, float R, float B, Edges Unusable)
    {
        public float Cx => (L + R) / 2f;
        public float Cy => (T + B) / 2f;
    }

    private sealed class TrackHistory
    {
        public readonly List<Sample> Samples = [];
        public DateTime FirstSeenUtc;
        public bool Moving;
        // For the summary of a track that travelled but never counted as moving (TakeMissedTracks).
        public string? Label;
        public float FirstCx, FirstCy;
        public int Observations;
        public bool EverMoving;
        public double LastJitter, LastThreshold, BestTrend;
        // A longer-running estimate of the wobble (per axis, as a variance) than the few seconds of
        // samples kept: a short stretch can happen to be calm, and the estimate must not dip with it.
        public double WobbleVarX, WobbleVarY;
        public int WobbleCount;
        public BestFrame? Best;
        public double BestScore = double.MinValue;
    }

    /// <summary>
    /// Standard mode: every tracked box in the frame about to be observed, so an edge lying inside
    /// another object's box (a person walking past a parked car) isn't read as that car's own edge
    /// moving. Call once per frame before the <see cref="Observe"/> calls; without it no edge counts
    /// as covered.
    /// </summary>
    public void BeginFrame(IEnumerable<(int TrackId, SKRectI Box)> boxes) => _frameBoxes = boxes.ToList();

    /// <summary>
    /// Records one track's observation for this frame and returns its current classification.
    /// <paramref name="boundingBox"/> is in the same pixel coordinate space as
    /// <paramref name="frameWidth"/>/<paramref name="frameHeight"/> (the decoded frame's own
    /// resolution) — normalized internally for the stored best frame's box.
    /// </summary>
    public TrackObservation Observe(int trackId, SKRectI boundingBox, double confidence,
        int frameWidth, int frameHeight, DateTime nowUtc, string? label = null)
    {
        if (!_tracks.TryGetValue(trackId, out var history))
        {
            history = new TrackHistory
            {
                FirstSeenUtc = nowUtc,
                FirstCx = (boundingBox.Left + boundingBox.Right) / 2f,
                FirstCy = (boundingBox.Top + boundingBox.Bottom) / 2f,
            };
            _tracks[trackId] = history;
        }
        history.Observations++;
        if (label is not null) history.Label = label;

        history.Samples.Add(new Sample(nowUtc, boundingBox.Left, boundingBox.Top, boundingBox.Right, boundingBox.Bottom,
            UnusableEdges(trackId, boundingBox, frameWidth, frameHeight)));

        // Drop samples older than the mode's history — and as a safety bound, never let the buffer
        // grow past MaxHistorySamples regardless of timestamps.
        var keep = _options.Mode == MovementMode.Standard ? _options.StandardHistory : _options.MovementWindow;
        var cutoff = nowUtc - keep;
        history.Samples.RemoveAll(s => s.AtUtc < cutoff);
        if (history.Samples.Count > _options.MaxHistorySamples)
        {
            history.Samples.RemoveRange(0, history.Samples.Count - _options.MaxHistorySamples);
        }

        MovementState state;
        string? reason = null;
        if (_options.Mode == MovementMode.Standard)
        {
            var wasMoving = history.Moving;
            var fired = ClassifyStandard(history, boundingBox, nowUtc);
            history.Moving = fired is not null;
            history.EverMoving |= history.Moving;
            state = history.Moving ? MovementState.Moving : MovementState.Idle;
            if (history.Moving && !wasMoving)
                reason = $"{fired}, {(nowUtc - history.FirstSeenUtc).TotalSeconds:0.00}s after the track started";
        }
        else
        {
            state = ClassifyLegacy(history, boundingBox);
        }

        UpdateBestFrame(history, boundingBox, confidence, frameWidth, frameHeight, nowUtc);

        return new TrackObservation(trackId, state, reason);
    }

    /// <summary>Proposed default scoring for decision 10's "clearest/largest frame" selection —
    /// pure and independently testable so it's cheap to retune (see the plan's open questions).
    /// Rewards a box that's both confident and large; the product means doubling either factor
    /// doubles the score, rather than one dominating the other. Public (not just used internally)
    /// because LarisVMS.Vision.Service's own per-label (not just per-track) best-frame tracking
    /// reuses the identical formula — see CameraDetectionPipeline.</summary>
    public static double Score(double confidence, double normalizedBoxArea) => confidence * normalizedBoxArea;

    // ── Legacy ────────────────────────────────────────────────────────────

    private MovementState ClassifyLegacy(TrackHistory history, SKRectI currentBox)
    {
        var diagonal = MathF.Sqrt((float)(currentBox.Width * currentBox.Width + currentBox.Height * currentBox.Height));
        if (diagonal <= 0f) return MovementState.Idle;

        if (!_options.JitterRejectionEnabled)
        {
            // Original behaviour — the Detection.RejectMotionJitter "off" path.
            if (history.Samples.Count < 2) return MovementState.Idle;
            var first = history.Samples[0];
            var last = history.Samples[^1];
            var d = Distance(first.Cx, first.Cy, last.Cx, last.Cy);
            return d / diagonal >= _options.MovementThresholdFraction ? MovementState.Moving : MovementState.Idle;
        }

        // Need a few samples before the smoothed-endpoint analysis means anything.
        if (history.Samples.Count < 3) return MovementState.Idle;

        // Smooth each end over up to two samples rather than trusting a single one — a stationary
        // object's raw detection box jitters frame-to-frame, and one bad sample at either endpoint
        // would otherwise spike the displacement over the threshold. Deliberately light: averaging
        // a full third of the window each side (the original 0.188.0 form) understated a real
        // walker's net travel enough to read as Idle.
        var cluster = Math.Min(2, history.Samples.Count / 2);
        var (sx, sy) = MeanCentroid(history.Samples, 0, cluster);
        var (ex, ey) = MeanCentroid(history.Samples, history.Samples.Count - cluster, cluster);
        var net = Distance(sx, sy, ex, ey);

        if (net < _options.MinNetDisplacementPixels) return MovementState.Idle;
        if (net / diagonal < _options.MovementThresholdFraction) return MovementState.Idle;

        return MovementState.Moving;
    }

    private static (float X, float Y) MeanCentroid(List<Sample> samples, int start, int count)
    {
        float sumX = 0f, sumY = 0f;
        for (var i = start; i < start + count; i++)
        {
            sumX += samples[i].Cx;
            sumY += samples[i].Cy;
        }
        return (sumX / count, sumY / count);
    }

    // ── Standard ──────────────────────────────────────────────────────────

    private readonly record struct Thresholds(double C, double TTranslate, double TSlow, double TEdge, double FastJitter);

    private Thresholds ForSensitivity() => _options.Sensitivity switch
    {
        MovementSensitivity.Low => new Thresholds(3.5, 4, 8, 5, 10),
        MovementSensitivity.High => new Thresholds(1.8, 2.5, 5, 3, 6.5),
        _ => new Thresholds(2.5, 3, 6, 4, 8),
    };

    // Fraction of the frame within which a box edge counts as cut off by the frame border.
    private const float BorderMargin = 0.015f;
    // Fast path: each step within this angle of the overall direction (cos 50°).
    private const double StepDirectionCos = 0.6428;
    private static readonly TimeSpan EmergingAge = TimeSpan.FromSeconds(2);
    private static readonly int[] FitSpans = [5, 8, 12, 18, 27, 40, 60];

    /// <summary>The rule that says this track is Moving, or null for Idle. Once Moving, the
    /// distance thresholds are halved and the trend ones eased (hysteresis), so an object slowing to
    /// a stop doesn't flicker.</summary>
    private string? ClassifyStandard(TrackHistory history, SKRectI currentBox, DateTime nowUtc)
    {
        var samples = history.Samples;
        if (samples.Count < 3) return history.Moving ? "held" : null;

        var diag = Math.Sqrt((double)currentBox.Width * currentBox.Width + (double)currentBox.Height * currentBox.Height);
        if (diag <= 0) return null;

        var th = ForSensitivity();
        var ease = history.Moving ? 0.5 : 1.0;
        var tEase = history.Moving ? 0.67 : 1.0;
        var floor = _options.MinNetDisplacementPixels;
        // This track's own box wobble, measured from its history. Until it has a few dozen samples
        // the estimate is rough, so a higher floor applies.
        UpdateWobble(history);
        var measured = Math.Max(JitterSigma(samples), Math.Sqrt(Math.Max(history.WobbleVarX, history.WobbleVarY)));
        var jitter = Math.Max(measured, (samples.Count < 10 ? 0.02 : 0.01) * diag);
        var threshold = Math.Max(Math.Max(floor, Math.Min(0.5 * diag, th.C * Math.Sqrt(diag))), 5 * jitter) * ease;
        history.LastJitter = jitter;
        history.LastThreshold = threshold;

        // Within a few frames, for every track: the last 3-5 samples each step the same way, roughly
        // in a line, each step clearly bigger than this box's own wobble, and together far bigger
        // (Thresholds.FastJitter times it — over a few steps wobble shows as about √2 of it, so that
        // is several times what wobble ever reaches). Sized from the wobble rather than the box, so a
        // large truck pulling out counts as soon as its steps outgrow its wobble, as fast as a small
        // car. A box that jumps between two shapes fails: the jump is one step, the rest stand still.
        var fastJitter = Math.Max(measured, (samples.Count < 10 ? 0.012 : 0.005) * diag);
        // At least 6 % of the box's size too: a distant parked car's box can creep a few pixels in
        // a row, more than its tiny measured wobble.
        var fastThreshold = Math.Max(Math.Max(floor, 0.06 * diag), th.FastJitter * fastJitter) * ease;
        // Entering at the frame border, or coming out from behind something, only happens to a track
        // that has just appeared. An old track whose box is cut off at the border (a shelf against the
        // edge of the picture, a car parked half out of view) just changes shape there.
        var young = nowUtc - history.FirstSeenUtc < EmergingAge;
        var stepMin = Math.Max(floor * 0.5, 2 * fastJitter) * ease;
        for (var k = 3; k <= Math.Min(5, samples.Count); k++)
        {
            if (SizeHolds(samples, k) && ConsistentSteps(samples, k, fastThreshold, stepMin, s => s.Cx, s => s.Cy, Edges.None)) return $"steps({k}): needed {fastThreshold:0}px, wobble {fastJitter:0.0}px";
            foreach (var (edge, opposite, value, horizontal) in EdgeSignals)
            {
                // A box cut off at the frame border: its other edge shows the real speed. A single
                // edge wobbles more than the centre (which averages two), hence the 1.4.
                if (!young || !EdgeUsable(samples, k, edge) || !EdgeClippedAny(samples, k, opposite)) continue;
                if (ConsistentSteps(samples, k, fastThreshold * 1.4, stepMin * 1.4,
                        horizontal ? value : _ => 0f, horizontal ? _ => 0f : value, edge)) return $"edge-steps({k}): needed {fastThreshold * 1.4:0}px, wobble {fastJitter:0.0}px";
            }
        }

        // The span checks below need the fit's own, more cautious distance.
        var fastSpanThreshold = Math.Max(threshold, 8 * jitter * ease);

        // Straight-line trend over longer spans, for movement too small per step for the fast path.
        foreach (var requested in FitSpans.Append(samples.Count))
        {
            var k = Math.Min(requested, samples.Count);
            if (k < 5) continue;
            var start = samples.Count - k;
            var duration = (samples[^1].AtUtc - samples[start].AtUtc).TotalSeconds;
            if (duration <= 0) continue;

            var fx = Fit(samples, start, s => s.Cx);
            var fy = Fit(samples, start, s => s.Cy);
            var centreTravel = Math.Sqrt(fx.Slope * fx.Slope + fy.Slope * fy.Slope) * duration;
            var centreT = Math.Sqrt(fx.Slope * fx.Slope + fy.Slope * fy.Slope)
                / Math.Max(1e-9, Math.Sqrt(fx.StdErr * fx.StdErr + fy.StdErr * fy.StdErr));

            // A line through only a handful of points can look like a trend by chance, so short spans
            // need the fast path's distance and a clearer trend.
            var shortSpan = k < 8;
            var spanThreshold = shortSpan ? fastSpanThreshold : threshold;
            var spanT = th.TTranslate * tEase * (shortSpan ? 1.3 : 1.0);
            // Along the fitted direction, so "progresses" can be checked on one number.
            var dirLength = Math.Sqrt(fx.Slope * fx.Slope + fy.Slope * fy.Slope);
            var ux = dirLength > 0 ? fx.Slope / dirLength : 0;
            var uy = dirLength > 0 ? fy.Slope / dirLength : 0;
            var centreProgresses = dirLength > 0 && Progresses(samples, start, s => s.Cx * ux + s.Cy * uy);
            history.BestTrend = Math.Max(history.BestTrend, centreT);
            // The centre also moves when the box only changes shape — one side growing or shrinking,
            // as on an object cut off at the frame edge. That isn't the object going anywhere.
            var shapeOnly = ShapeChange(samples, start) > 0.6 * centreTravel;

            if (!shapeOnly && centreTravel >= spanThreshold && centreT >= spanT && centreProgresses) return $"trend({k})";
            // A slow, steady drift. At least 15 % of the box's size: a parked car's box can creep a
            // little over a few seconds. The strictest progress check: in six runs, one jump leaves
            // four of the five gaps flat.
            if (!shapeOnly && duration >= 1.2 && k >= 12 && centreTravel >= Math.Max(floor, 0.15 * diag) * ease && centreT >= th.TSlow * tEase
                && Progresses(samples, start, s => s.Cx * ux + s.Cy * uy, parts: 6))
                return $"steady({k})";

            // A single edge wobbles more than the centre, which averages two.
            var edgeThreshold = spanThreshold * 1.4;
            var edgeT = th.TEdge * tEase * (shortSpan ? 1.3 : 1.0);

            // Both opposite edges travelling together: the whole box moved.
            if (PairMoves(samples, start, duration, Edges.Left, Edges.Right, s => s.L, s => s.R, edgeThreshold, spanT)
                || PairMoves(samples, start, duration, Edges.Top, Edges.Bottom, s => s.T, s => s.B, edgeThreshold, spanT))
                return $"edges({k})";

            foreach (var (edge, opposite, value, _) in EdgeSignals)
            {
                if (!young || !EdgeUsable(samples, k, edge)) continue;
                var fe = Fit(samples, start, value);
                if (Math.Abs(fe.Slope) * duration < edgeThreshold || fe.T < edgeT) continue;
                if (!Progresses(samples, start, s => value(s) * Math.Sign(fe.Slope))) continue;
                // Entering at the frame border.
                if (EdgeClippedAny(samples, k, opposite)) return $"entering({k})";
                // Coming out from behind something: a new track whose far edge holds still.
                if (EdgeUsable(samples, k, opposite)
                    && Math.Abs(Fit(samples, start, OppositeValue(opposite)).Slope) * duration < threshold * 0.25)
                    return $"emerging({k})";
            }

            // Growing or shrinking: coming straight at the camera or going away.
            if (EdgeUsable(samples, k, Edges.Bottom) && EdgeUsable(samples, k, Edges.Top))
            {
                // Coming closer moves the bottom of the box down the picture as it grows (and up as it
                // shrinks going away); a box that only grows at the top or one side, like a plant's,
                // isn't an approaching object.
                var fh = Fit(samples, start, s => MathF.Log(Math.Max(1f, s.B - s.T)));
                var fb = Fit(samples, start, s => s.B);
                var heightNow = samples[^1].B - samples[^1].T;
                var heightChange = Math.Abs(Math.Exp(fh.Slope * duration) - 1) * heightNow;
                if (Math.Abs(fh.Slope) * duration >= 0.12 * ease && fh.T >= edgeT
                    && Math.Sign(fb.Slope) == Math.Sign(fh.Slope) && Math.Abs(fb.Slope) * duration >= Math.Max(floor, 0.25 * heightChange)
                    && Progresses(samples, start, s => MathF.Log(Math.Max(1f, s.B - s.T)) * Math.Sign(fh.Slope)))
                    return $"scale({k})";
            }
        }

        // Anything the earlier rule would have called moving — its centre travelled half its box size
        // within 2 s — still is, provided it got there by moving rather than by one jump.
        var legacyStart = samples.FindIndex(s => nowUtc - s.AtUtc <= LegacyWindow);
        if (legacyStart >= 0 && samples.Count - legacyStart >= 3)
        {
            var a = samples[legacyStart];
            var b = samples[^1];
            double dx = b.Cx - a.Cx, dy = b.Cy - a.Cy;
            var net = Math.Sqrt(dx * dx + dy * dy);
            if (net >= Math.Max(floor, 0.5 * diag * ease) && ShapeChange(samples, legacyStart) <= 0.6 * net
                && Progresses(samples, legacyStart, s => (s.Cx * dx + s.Cy * dy) / net))
                return "half-box";
        }

        return null;
    }

    private static readonly TimeSpan LegacyWindow = TimeSpan.FromSeconds(2);

    /// <summary>How much the box's width or height changed from the start of the span to its end, in
    /// pixels: the larger of the two, from the medians of the first and last quarter of the samples so
    /// one odd box doesn't decide it.</summary>
    private static double ShapeChange(List<Sample> samples, int start)
    {
        var n = samples.Count - start;
        var q = Math.Max(1, n / 4);
        double MedianOf(int from, Func<Sample, double> value)
        {
            var values = new List<double>(q);
            for (var i = from; i < from + q; i++) values.Add(value(samples[i]));
            return Median(values);
        }
        var dw = Math.Abs(MedianOf(samples.Count - q, s => s.R - s.L) - MedianOf(start, s => s.R - s.L));
        var dh = Math.Abs(MedianOf(samples.Count - q, s => s.B - s.T) - MedianOf(start, s => s.B - s.T));
        return Math.Max(dw, dh);
    }

    /// <summary>Whether <paramref name="value"/> (already signed so that "further" is larger)
    /// genuinely progresses from <paramref name="start"/> to the end: split into runs of samples
    /// (four unless asked for more), each run's median is further along than the one before by a fair
    /// share of the whole change. Real movement gets there a bit at a time; a box that jumps between two shapes (a
    /// plant's leaves, a parked car as something passes in front of it, the tracker swapping two
    /// objects) gets there in one step, and wherever that step falls, at least one of the three gaps
    /// between runs is flat. Medians, so one bad box doesn't decide it.</summary>
    private static bool Progresses(List<Sample> samples, int start, Func<Sample, double> value, int parts = 4)
    {
        var n = samples.Count - start;
        if (n < parts) return false;
        var medians = new double[parts];
        for (var part = 0; part < parts; part++)
        {
            var from = start + part * n / parts;
            var to = start + (part + 1) * n / parts;
            var run = new List<double>(to - from);
            for (var i = from; i < to; i++) run.Add(value(samples[i]));
            medians[part] = Median(run);
        }
        var total = medians[parts - 1] - medians[0];
        if (total <= 0) return false;
        var share = 0.45 / (parts - 1);
        for (var part = 1; part < parts; part++)
            if (medians[part] - medians[part - 1] < share * total) return false;
        return true;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var n = values.Count;
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2;
    }

    private static readonly (Edges Edge, Edges Opposite, Func<Sample, float> Value, bool Horizontal)[] EdgeSignals =
    [
        (Edges.Left, Edges.Right, s => s.L, true),
        (Edges.Right, Edges.Left, s => s.R, true),
        (Edges.Top, Edges.Bottom, s => s.T, false),
        (Edges.Bottom, Edges.Top, s => s.B, false),
    ];

    private static Func<Sample, float> OppositeValue(Edges edge) => edge switch
    {
        Edges.Left => s => s.L,
        Edges.Right => s => s.R,
        Edges.Top => s => s.T,
        _ => s => s.B,
    };

    /// <summary>The last <paramref name="k"/> samples step consistently: net travel at least
    /// <paramref name="threshold"/>, and every step longer than <paramref name="stepMin"/> and
    /// within 50° of the overall direction. Jitter can't keep that up for 2-3 steps.</summary>
    /// <summary>The box keeps its size over the last <paramref name="k"/> samples, compared with how
    /// far its centre went: something moving across the picture does, while a box that jumps between
    /// two shapes (a plant with or without its leaves, a parked car cut back as something passes in
    /// front of it) changes size by more than its centre moves. Growing toward the camera or entering
    /// at the frame edge are their own rules.</summary>
    private static bool SizeHolds(List<Sample> samples, int k)
    {
        var start = samples.Count - k;
        var first = samples[start];
        var last = samples[^1];
        double dx = last.Cx - first.Cx, dy = last.Cy - first.Cy;
        var net = Math.Sqrt(dx * dx + dy * dy);
        for (var i = start + 1; i < samples.Count; i++)
        {
            var s = samples[i];
            if (Math.Abs((s.R - s.L) - (first.R - first.L)) > 0.5 * net) return false;
            if (Math.Abs((s.B - s.T) - (first.B - first.T)) > 0.5 * net) return false;
        }
        return true;
    }

    private static bool ConsistentSteps(List<Sample> samples, int k, double threshold, double stepMin,
        Func<Sample, float> x, Func<Sample, float> y, Edges needsUsable)
    {
        var start = samples.Count - k;
        double nx = x(samples[^1]) - x(samples[start]), ny = y(samples[^1]) - y(samples[start]);
        var net = Math.Sqrt(nx * nx + ny * ny);
        if (net < threshold) return false;

        // Frames arrive unevenly (one is skipped whenever detection runs long), so each step is
        // judged against its own gap: a step over a short gap may be short, one over a long gap has
        // to be longer.
        var gaps = new double[k - 1];
        for (var i = start + 1; i < samples.Count; i++) gaps[i - start - 1] = (samples[i].AtUtc - samples[i - 1].AtUtc).TotalSeconds;
        var sortedGaps = gaps.Order().ToArray();
        var typicalGap = sortedGaps[sortedGaps.Length / 2];
        if (typicalGap <= 0) return false;

        double minSpeed = double.MaxValue, maxSpeed = 0;
        for (var i = start + 1; i < samples.Count; i++)
        {
            var gap = gaps[i - start - 1];
            if (gap <= 0) return false;
            double sx = x(samples[i]) - x(samples[i - 1]), sy = y(samples[i]) - y(samples[i - 1]);
            var step = Math.Sqrt(sx * sx + sy * sy);
            if (step < stepMin * Math.Clamp(gap / typicalGap, 0.5, 1.5)) return false;
            if ((sx * nx + sy * ny) / (step * net) < StepDirectionCos) return false;
            minSpeed = Math.Min(minSpeed, step / gap);
            maxSpeed = Math.Max(maxSpeed, step / gap);
            // Somewhat straight: each middle point sits near the line between its neighbours, at its
            // own time.
            if (i < samples.Count - 1)
            {
                var span = (samples[i + 1].AtUtc - samples[i - 1].AtUtc).TotalSeconds;
                var w = (samples[i].AtUtc - samples[i - 1].AtUtc).TotalSeconds / span;
                double bx = x(samples[i]) - (x(samples[i - 1]) + (x(samples[i + 1]) - x(samples[i - 1])) * w);
                double by = y(samples[i]) - (y(samples[i - 1]) + (y(samples[i + 1]) - y(samples[i - 1])) * w);
                if (Math.Sqrt(bx * bx + by * by) > 0.25 * net) return false;
            }
        }
        // Moving at a roughly even pace. A box that jumps between two shapes makes one big step next
        // to small ones.
        if (maxSpeed > 3 * minSpeed) return false;
        return needsUsable == Edges.None || EdgeUsable(samples, k, needsUsable);
    }

    /// <summary>How much the box centre wobbles from frame to frame, in pixels (one standard
    /// deviation, the larger of the two axes), estimated from second differences: zero for steady
    /// movement in a straight line, large for wobble, so a real mover doesn't inflate it.
    /// Percentile-based, so a single bad box doesn't either.</summary>
    private static double JitterSigma(List<Sample> samples)
    {
        if (samples.Count < 3) return 0;
        return Math.Max(AxisJitter(samples, s => s.Cx), AxisJitter(samples, s => s.Cy));
    }

    // About 14 s of samples at 7 fps.
    private const int WobbleMemory = 100;

    /// <summary>Folds the newest middle sample's residual into the track's long-running wobble
    /// estimate: a running mean for its first samples, then an average weighted toward the last
    /// <see cref="WobbleMemory"/> or so.</summary>
    private static void UpdateWobble(TrackHistory history)
    {
        var samples = history.Samples;
        if (samples.Count < 3) return;
        var i = samples.Count - 2;
        var rx = Residual(samples, i, s => s.Cx);
        var ry = Residual(samples, i, s => s.Cy);
        history.WobbleCount = Math.Min(history.WobbleCount + 1, WobbleMemory);
        var a = 1.0 / history.WobbleCount;
        history.WobbleVarX += a * (rx * rx - history.WobbleVarX);
        history.WobbleVarY += a * (ry * ry - history.WobbleVarY);
    }

    /// <summary>Sample <paramref name="i"/>'s distance from the straight line between its
    /// neighbours at its own time, scaled so that for independent wobble of deviation s it has
    /// deviation s.</summary>
    private static double Residual(List<Sample> samples, int i, Func<Sample, float> value)
    {
        var span = (samples[i + 1].AtUtc - samples[i - 1].AtUtc).TotalSeconds;
        var w = span > 0 ? (samples[i].AtUtc - samples[i - 1].AtUtc).TotalSeconds / span : 0.5;
        var expected = value(samples[i - 1]) + (value(samples[i + 1]) - value(samples[i - 1])) * w;
        return (value(samples[i]) - expected) / Math.Sqrt(1 + w * w + (1 - w) * (1 - w));
    }

    private static double AxisJitter(List<Sample> samples, Func<Sample, float> value)
    {
        // Each middle sample's distance from the straight line between its neighbours, at its own
        // time: zero for steady movement however unevenly the frames arrive (frames get skipped
        // whenever detection runs long), so a fast mover isn't mistaken for a wobbly one.
        double sumSquares = 0;
        for (var i = 1; i < samples.Count - 1; i++)
        {
            var r = Residual(samples, i, value);
            sumSquares += r * r;
        }
        // Root mean square rather than a percentile: steadier with the few dozen values a track has
        // (a percentile came out at half the real wobble often enough to matter), and an odd bad box
        // only raises it, which is the safe side here.
        return Math.Sqrt(sumSquares / (samples.Count - 2));
    }

    private static bool PairMoves(List<Sample> samples, int start, double duration, Edges a, Edges b,
        Func<Sample, float> va, Func<Sample, float> vb, double threshold, double tMin)
    {
        var k = samples.Count - start;
        if (!EdgeUsable(samples, k, a) || !EdgeUsable(samples, k, b)) return false;
        var fa = Fit(samples, start, va);
        var fb = Fit(samples, start, vb);
        return Math.Sign(fa.Slope) == Math.Sign(fb.Slope) && fa.Slope != 0
            && Math.Abs(fa.Slope) * duration >= threshold && Math.Abs(fb.Slope) * duration >= threshold
            && fa.T >= tMin && fb.T >= tMin
            && Progresses(samples, start, s => va(s) * Math.Sign(fa.Slope))
            && Progresses(samples, start, s => vb(s) * Math.Sign(fb.Slope));
    }

    private static bool EdgeUsable(List<Sample> samples, int k, Edges edge)
    {
        for (var i = samples.Count - k; i < samples.Count; i++)
            if ((samples[i].Unusable & edge) != 0) return false;
        return true;
    }

    private static bool EdgeClippedAny(List<Sample> samples, int k, Edges edge)
    {
        // Only the frame border counts here, not another object's box.
        for (var i = samples.Count - k; i < samples.Count; i++)
            if ((samples[i].Unusable & (Edges)((int)edge << 4)) != 0) return true;
        return false;
    }

    /// <summary>Least-squares line through (time, value): slope per second, its standard error, and
    /// t = |slope| / standard error — how clearly the samples trend rather than scatter.</summary>
    internal readonly record struct LineFit(double Slope, double StdErr)
    {
        public double T => Math.Abs(Slope) / Math.Max(1e-9, StdErr);
    }

    private static LineFit Fit(List<Sample> samples, int start, Func<Sample, float> value)
    {
        var n = samples.Count - start;
        var t0 = samples[start].AtUtc;
        double meanT = 0, meanV = 0;
        for (var i = start; i < samples.Count; i++)
        {
            meanT += (samples[i].AtUtc - t0).TotalSeconds;
            meanV += value(samples[i]);
        }
        meanT /= n;
        meanV /= n;
        double sxx = 0, sxy = 0;
        for (var i = start; i < samples.Count; i++)
        {
            var dt = (samples[i].AtUtc - t0).TotalSeconds - meanT;
            sxx += dt * dt;
            sxy += dt * (value(samples[i]) - meanV);
        }
        if (sxx <= 0) return new LineFit(0, double.PositiveInfinity);
        var slope = sxy / sxx;
        double ssr = 0;
        for (var i = start; i < samples.Count; i++)
        {
            var predicted = meanV + slope * ((samples[i].AtUtc - t0).TotalSeconds - meanT);
            var r = value(samples[i]) - predicted;
            ssr += r * r;
        }
        // Boxes are whole pixels: half a pixel of rounding is the least scatter a real fit has, so a
        // perfectly straight synthetic or quantized track doesn't read as infinitely certain.
        var variance = Math.Max(ssr / Math.Max(1, n - 2), 0.25 / 12);
        return new LineFit(slope, Math.Sqrt(variance / sxx));
    }

    /// <summary>Edges that can't be trusted to show this object's own movement in this frame: cut
    /// off by the frame border (also flagged as …AtBorder, for the entering rule), or lying inside
    /// another tracked object's box.</summary>
    private Edges UnusableEdges(int trackId, SKRectI box, int frameWidth, int frameHeight)
    {
        var flags = Edges.None;
        if (frameWidth > 0 && frameHeight > 0)
        {
            var mx = frameWidth * BorderMargin;
            var my = frameHeight * BorderMargin;
            if (box.Left <= mx) flags |= Edges.Left;
            if (box.Right >= frameWidth - mx) flags |= Edges.Right;
            if (box.Top <= my) flags |= Edges.Top;
            if (box.Bottom >= frameHeight - my) flags |= Edges.Bottom;
            flags |= (Edges)((int)flags << 4);
        }

        foreach (var (otherId, other) in _frameBoxes)
        {
            if (otherId == trackId) continue;
            const int pad = 2;
            var midY = (box.Top + box.Bottom) / 2;
            var midX = (box.Left + box.Right) / 2;
            bool Inside(int x, int y) => x >= other.Left - pad && x <= other.Right + pad && y >= other.Top - pad && y <= other.Bottom + pad;
            if (Inside(box.Left, midY)) flags |= Edges.Left;
            if (Inside(box.Right, midY)) flags |= Edges.Right;
            if (Inside(midX, box.Top)) flags |= Edges.Top;
            if (Inside(midX, box.Bottom)) flags |= Edges.Bottom;
        }
        return flags;
    }

    private static float Distance(float ax, float ay, float bx, float by) =>
        MathF.Sqrt(MathF.Pow(bx - ax, 2) + MathF.Pow(by - ay, 2));

    // ── Best frame ────────────────────────────────────────────────────────

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
        foreach (var trackId in toRemove)
        {
            if (_options.Mode == MovementMode.Standard && MissedSummary(trackId, _tracks[trackId]) is { } summary)
                _missed.Add(summary);
            _tracks.Remove(trackId);
        }
    }

    private readonly List<string> _missed = [];

    /// <summary>Standard mode: one line for each track that has ended since the last call after
    /// clearly going somewhere — its centre ended up at least half its box size from where it was
    /// first seen — without ever counting as moving. What a camera's log shows when movement is
    /// missed, so the reason can be read off without debug logging.</summary>
    public IReadOnlyList<string> TakeMissedTracks()
    {
        if (_missed.Count == 0) return [];
        var taken = _missed.ToList();
        _missed.Clear();
        return taken;
    }

    private static string? MissedSummary(int trackId, TrackHistory h)
    {
        if (h.EverMoving || h.Samples.Count < 2) return null;
        var last = h.Samples[^1];
        double w = last.R - last.L, ht = last.B - last.T;
        var diag = Math.Sqrt(w * w + ht * ht);
        double dx = last.Cx - h.FirstCx, dy = last.Cy - h.FirstCy;
        var travel = Math.Sqrt(dx * dx + dy * dy);
        if (diag <= 0 || travel < 0.5 * diag) return null;

        var gaps = new List<double>();
        for (var i = 1; i < h.Samples.Count; i++) gaps.Add((h.Samples[i].AtUtc - h.Samples[i - 1].AtUtc).TotalSeconds);
        gaps.Sort();
        var medianGap = gaps.Count == 0 ? 0 : gaps[gaps.Count / 2];
        var lifetime = (last.AtUtc - h.FirstSeenUtc).TotalSeconds;
        return $"track {trackId} ({h.Label ?? "object"}): travelled {travel:0}px with a {diag:0}px box over {lifetime:0.0}s " +
            $"({h.Observations} samples, median gap {medianGap:0.00}s); wobble {h.LastJitter:0.0}px, " +
            $"distance needed {h.LastThreshold:0}px, best trend {h.BestTrend:0.0}";
    }
}
