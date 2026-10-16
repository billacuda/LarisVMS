using LarisVMS.Core.Dtos;

namespace LarisVMS.Vision.Detection;

/// <summary>One completed speed measurement. AtUtc is when the object crossed its second gate
/// (interpolated between frames).</summary>
public sealed record SpeedMeasurement(Guid TrapId, double Kph, DateTime AtUtc);

/// <summary>
/// Times tracked objects between the two gate lines of each speed trap. Fed one ground point per
/// track per frame (the bottom centre of its box, where it meets the road — the gates are drawn on
/// the ground, so the box centre would read late on the near gate and early on the far one). Each
/// step from the previous point to this one is tested against both gates; a crossing's time is
/// interpolated along the step, so the sub-stream's frame interval doesn't quantize the result.
/// Either order counts (A then B, or B then A). One measurement per track per trap.
/// </summary>
public sealed class SpeedTrapMeter(IReadOnlyList<VisionSpeedTrapDto> traps)
{
    /// <summary>Faster than a gate pair can honestly be timed at sub-stream frame rates.</summary>
    public static readonly TimeSpan MinElapsed = TimeSpan.FromSeconds(0.15);
    public const double MaxKph = 400;

    private sealed class TrackState
    {
        public double X, Y;
        public DateTime At;
        public readonly Dictionary<Guid, (DateTime? A, DateTime? B, bool Done)> Crossings = [];
    }

    private readonly Dictionary<int, TrackState> _tracks = [];

    public bool HasTraps => traps.Count > 0;

    /// <summary>Feeds one ground point (normalized 0-1) for a track. Returns a measurement the
    /// moment the track completes a trap, otherwise null.</summary>
    public SpeedMeasurement? Observe(int trackId, double x, double y, DateTime atUtc)
    {
        if (traps.Count == 0) return null;
        if (!_tracks.TryGetValue(trackId, out var state))
        {
            _tracks[trackId] = new TrackState { X = x, Y = y, At = atUtc };
            return null;
        }
        if (atUtc <= state.At) return null;

        SpeedMeasurement? result = null;
        foreach (var trap in traps)
        {
            var c = state.Crossings.GetValueOrDefault(trap.Id);
            if (c.Done) continue;

            if (c.A is null && Cross(state.X, state.Y, x, y, trap.AX1, trap.AY1, trap.AX2, trap.AY2) is { } ua)
                c.A = state.At + (atUtc - state.At) * ua;
            if (c.B is null && Cross(state.X, state.Y, x, y, trap.BX1, trap.BY1, trap.BX2, trap.BY2) is { } ub)
                c.B = state.At + (atUtc - state.At) * ub;

            if (c is { A: { } ta, B: { } tb })
            {
                c.Done = true;
                var elapsed = (ta > tb ? ta - tb : tb - ta);
                if (elapsed >= MinElapsed && trap.DistanceMeters > 0)
                {
                    var kph = trap.DistanceMeters / elapsed.TotalSeconds * 3.6;
                    if (kph <= MaxKph) result ??= new SpeedMeasurement(trap.Id, Math.Round(kph, 1), ta > tb ? ta : tb);
                }
            }
            state.Crossings[trap.Id] = c;
        }

        state.X = x;
        state.Y = y;
        state.At = atUtc;
        return result;
    }

    /// <summary>Forgets tracks the tracker no longer has.</summary>
    public void Prune(IReadOnlySet<int> liveTrackIds)
    {
        foreach (var id in _tracks.Keys)
        {
            if (!liveTrackIds.Contains(id)) _tracks.Remove(id);
        }
    }

    /// <summary>Where along the step p0→p1 (0-1) it crosses segment q0→q1, or null if it doesn't.
    /// Touching an end of the gate counts.</summary>
    internal static double? Cross(double p0x, double p0y, double p1x, double p1y,
        double q0x, double q0y, double q1x, double q1y)
    {
        double rx = p1x - p0x, ry = p1y - p0y;
        double sx = q1x - q0x, sy = q1y - q0y;
        var denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-12) return null; // parallel
        double qpx = q0x - p0x, qpy = q0y - p0y;
        var t = (qpx * sy - qpy * sx) / denom; // along the step
        var u = (qpx * ry - qpy * rx) / denom; // along the gate
        return t is >= 0 and <= 1 && u is >= 0 and <= 1 ? t : null;
    }
}
