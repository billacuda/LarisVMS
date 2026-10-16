using LarisVMS.Core.Dtos;

namespace LarisVMS.Vision.Detection;

/// <summary>
/// Per-object bookkeeping behind each per-label span: every track that contributed to a span (was
/// moving, or reported idle), with its own best frame, colors and speed. A label's span report
/// carries the objects of that label — live ones plus ones whose track has since ended — and a
/// closed report releases the ended ones, so a later span of the same label starts clean.
///
/// Locked internally: the inference loop writes it every frame, and the pipeline's checkpoint loop
/// reads it from another task when it reports an in-progress span.
/// </summary>
public sealed class TrackObjectBook
{
    /// <summary>Most objects one span report carries; the rest are the least confident.</summary>
    public const int MaxObjectsPerReport = 20;

    /// <summary>An ended track no span report has claimed is dropped after this long.</summary>
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(10);

    /// <summary>A new best frame must beat the one the colors came from by this much, and be this
    /// far apart in time, before colors are classified again.</summary>
    private const double RecolorImprovement = 1.10;
    private static readonly TimeSpan RecolorMinGap = TimeSpan.FromSeconds(0.5);

    private sealed class Entry
    {
        public int TrackId;
        public string Label = "";
        public DateTime FirstSeen, LastSeen;
        public DateTime? BestAt;
        public double X, Y, W, H, Confidence, BestScore;
        public string? Primary, Upper, Lower;
        public double ColorScore;
        public DateTime? ColorAt;
        public double? SpeedKph;
        public Guid? SpeedTrapId;
        public DateTime? SpeedAt;

        public VisionDetectedObjectItem ToItem() => new(TrackId, Label, FirstSeen, LastSeen, BestAt,
            BestAt is null ? null : X, BestAt is null ? null : Y, BestAt is null ? null : W, BestAt is null ? null : H,
            BestAt is null ? null : Confidence, Primary, Upper, Lower, SpeedKph, SpeedTrapId, SpeedAt);
    }

    private readonly Dictionary<int, Entry> _live = [];
    private readonly List<Entry> _finished = [];
    // A speed measured before the track has an entry (not yet contributing to a span) — applied when
    // Observe creates it, so the measurement isn't lost; the meter only ever reports it once.
    private readonly Dictionary<int, SpeedMeasurement> _pendingSpeeds = [];
    private readonly Lock _gate = new();

    /// <summary>Records one contributing sighting of a track (box normalized 0-1). Returns true when
    /// this frame is clearly the best view of the object so far and its colors should be
    /// (re)classified from it.</summary>
    public bool Observe(int trackId, string label, DateTime atUtc,
        double x, double y, double w, double h, double confidence)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(trackId, out var e))
            {
                e = new Entry { TrackId = trackId, FirstSeen = atUtc };
                _live[trackId] = e;
                if (_pendingSpeeds.Remove(trackId, out var pending)) ApplySpeed(e, pending);
            }
            e.Label = label;
            if (atUtc > e.LastSeen) e.LastSeen = atUtc;

            var score = MovementClassifier.Score(confidence, Math.Clamp(w * h, 0, 1));
            if (score > e.BestScore)
            {
                e.BestScore = score;
                e.BestAt = atUtc;
                (e.X, e.Y, e.W, e.H, e.Confidence) = (x, y, w, h, confidence);
            }

            return e.ColorAt is null
                || (score >= e.ColorScore * RecolorImprovement && atUtc - e.ColorAt.Value >= RecolorMinGap);
        }
    }

    /// <summary>Stores colors classified from the frame Observe just flagged. A null result (IR,
    /// too small) still counts as an attempt, so a monochrome scene isn't re-sampled every frame,
    /// but it never overwrites colors already found.</summary>
    public void SetColors(int trackId, DateTime atUtc, ObjectColorResult colors)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(trackId, out var e)) return;
            e.ColorAt = atUtc;
            e.ColorScore = e.BestScore;
            if (colors.Primary is not null) e.Primary = colors.Primary;
            if (colors.Upper is not null) e.Upper = colors.Upper;
            if (colors.Lower is not null) e.Lower = colors.Lower;
        }
    }

    public void SetSpeed(int trackId, SpeedMeasurement measurement)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(trackId, out var e)) ApplySpeed(e, measurement);
            else _pendingSpeeds[trackId] = measurement;
        }
    }

    private static void ApplySpeed(Entry e, SpeedMeasurement measurement)
    {
        e.SpeedKph = measurement.Kph;
        e.SpeedTrapId = measurement.TrapId;
        e.SpeedAt = measurement.AtUtc;
    }

    public double? SpeedOf(int trackId)
    {
        lock (_gate) return _live.TryGetValue(trackId, out var e) ? e.SpeedKph : null;
    }

    public string? ColorOf(int trackId)
    {
        lock (_gate) return _live.TryGetValue(trackId, out var e) ? e.Primary : null;
    }

    /// <summary>Moves tracks the tracker has dropped to the ended list, and forgets ended ones
    /// that no span ever claimed.</summary>
    public void Prune(IReadOnlySet<int> liveTrackIds, DateTime nowUtc)
    {
        lock (_gate)
        {
            foreach (var (id, e) in _live)
            {
                if (liveTrackIds.Contains(id)) continue;
                _live.Remove(id);
                _finished.Add(e);
            }
            _finished.RemoveAll(e => nowUtc - e.LastSeen > FinishedRetention);
            foreach (var id in _pendingSpeeds.Keys)
            {
                if (!liveTrackIds.Contains(id)) _pendingSpeeds.Remove(id);
            }
        }
    }

    /// <summary>The objects behind a span of <paramref name="label"/> that started at
    /// <paramref name="spanStartUtc"/>, most confident first.</summary>
    public List<VisionDetectedObjectItem> ForLabel(string label, DateTime spanStartUtc)
    {
        lock (_gate)
        {
            return _live.Values.Concat(_finished)
                .Where(e => string.Equals(e.Label, label, StringComparison.OrdinalIgnoreCase) && e.LastSeen >= spanStartUtc)
                .OrderByDescending(e => e.BestScore)
                .Take(MaxObjectsPerReport)
                .Select(e => e.ToItem())
                .ToList();
        }
    }

    /// <summary>After a span of <paramref name="label"/> closes: its ended objects are reported
    /// for good.</summary>
    public void ReleaseFinished(string label)
    {
        lock (_gate) _finished.RemoveAll(e => string.Equals(e.Label, label, StringComparison.OrdinalIgnoreCase));
    }
}
