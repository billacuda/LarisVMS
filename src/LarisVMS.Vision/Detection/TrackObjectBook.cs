using LarisVMS.Core;
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
        public readonly List<(float X, float Y)> Path = [];

        public VisionDetectedObjectItem ToItem() => new(TrackId, Label, FirstSeen, LastSeen, BestAt,
            BestAt is null ? null : X, BestAt is null ? null : Y, BestAt is null ? null : W, BestAt is null ? null : H,
            BestAt is null ? null : Confidence, Primary, Upper, Lower, SpeedKph, SpeedTrapId, SpeedAt,
            Path.Count > 1 ? SceneHeat.FormatPath(Path) : null);
    }

    private readonly Dictionary<int, Entry> _live = [];
    private readonly List<Entry> _finished = [];
    // A speed measured before the track has an entry (not yet contributing to a span) — applied when
    // Observe creates it, so the measurement isn't lost; the meter only ever reports it once.
    private readonly Dictionary<int, SpeedMeasurement> _pendingSpeeds = [];
    private readonly Lock _gate = new();

    /// <summary>A path point is added once the box centre has moved this far (a share of the
    /// picture's width/height) since the last one.</summary>
    public const float PathStep = 0.005f;

    /// <summary>Most points one object's path keeps; when full, every other point is dropped.</summary>
    public const int MaxPathPoints = 120;

    /// <summary>Records one contributing sighting of a track (box normalized 0-1). While it is
    /// <paramref name="moving"/>, its box centre extends its path. Returns true when this frame is
    /// clearly the best view of the object so far and its colors should be (re)classified from it.</summary>
    public bool Observe(int trackId, string label, DateTime atUtc,
        double x, double y, double w, double h, double confidence, bool moving = true)
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
            if (moving) AddPathPoint(e.Path, (float)(x + w / 2), (float)(y + h / 2));

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

    internal static void AddPathPoint(List<(float X, float Y)> path, float x, float y)
    {
        if (path.Count > 0)
        {
            var (lx, ly) = path[^1];
            if (MathF.Sqrt((x - lx) * (x - lx) + (y - ly) * (y - ly)) < PathStep) return;
        }
        if (path.Count >= MaxPathPoints)
        {
            // Every other point, keeping the most recent so the path still ends where the object is.
            var kept = new List<(float X, float Y)>(MaxPathPoints / 2 + 1);
            for (var i = path.Count - 1; i >= 0; i -= 2) kept.Add(path[i]);
            kept.Reverse();
            path.Clear();
            path.AddRange(kept);
        }
        path.Add((x, y));
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

    /// <summary>Folds track <paramref name="fromId"/> into <paramref name="intoId"/>: the tracker
    /// found <paramref name="fromId"/> was <paramref name="intoId"/> coming back into view, so the two
    /// are one object — the earlier first sighting, both paths, the better best frame, and any colors
    /// or speed either has.</summary>
    public void Merge(int fromId, int intoId)
    {
        lock (_gate)
        {
            if (_pendingSpeeds.Remove(fromId, out var pendingSpeed) && !_pendingSpeeds.ContainsKey(intoId))
                _pendingSpeeds[intoId] = pendingSpeed;
            if (!_live.Remove(fromId, out var from)) return;
            if (!_live.TryGetValue(intoId, out var into))
            {
                // The hidden track never contributed (it wasn't counted yet): the young one simply takes its id.
                from.TrackId = intoId;
                _live[intoId] = from;
                if (_pendingSpeeds.Remove(intoId, out var pending)) ApplySpeed(from, pending);
                return;
            }

            if (from.FirstSeen < into.FirstSeen) into.FirstSeen = from.FirstSeen;
            if (from.LastSeen > into.LastSeen) into.LastSeen = from.LastSeen;
            foreach (var (x, y) in from.Path) AddPathPoint(into.Path, x, y);
            if (from.BestScore > into.BestScore)
            {
                (into.BestScore, into.BestAt) = (from.BestScore, from.BestAt);
                (into.X, into.Y, into.W, into.H, into.Confidence) = (from.X, from.Y, from.W, from.H, from.Confidence);
            }
            into.Primary ??= from.Primary;
            into.Upper ??= from.Upper;
            into.Lower ??= from.Lower;
            if (into.SpeedKph is null && from.SpeedKph is not null)
                (into.SpeedKph, into.SpeedTrapId, into.SpeedAt) = (from.SpeedKph, from.SpeedTrapId, from.SpeedAt);
        }
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
