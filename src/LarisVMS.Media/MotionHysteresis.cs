namespace LarisVMS.Media;

/// <summary>One completed motion span, ready to report — see MotionHysteresis.</summary>
public record MotionSpanResult(DateTime StartUtc, DateTime EndUtc, double PeakScore);

/// <summary>
/// Turns a per-frame stream of "did this frame cross the sensitivity threshold" ticks into
/// discrete spans, with debouncing on both edges. Without this, a single frame that trips the
/// threshold (a moth on the lens, a compression artifact) would open and close a span on its own —
/// at 5 fps that's a MotionSpan row every 200ms during anything mildly noisy, which is both useless
/// as a timeline signal and a meaningful write-rate problem at scale. One instance per zone, not
/// shared across zones on the same camera — each zone's motion state is independent.
///
/// Pure and stateful but I/O-free, so it's driven directly by a scripted sequence of timestamps in
/// LarisVMS.Tests rather than needing a live frame source.
/// </summary>
public sealed class MotionHysteresis(TimeSpan startAfter, TimeSpan endAfter)
{
    private DateTime? _motionSince;
    private DateTime? _quietSince;
    private DateTime? _spanStart;
    private double _peakScore;

    /// <summary>True while a confirmed span is currently open (i.e. between Observe calls that
    /// would return non-null). Deliberately read from a different async context than the one
    /// calling Observe (MotionSession.RunAsync's frame loop vs. NodeWorker's motion-decision loop);
    /// a benign, unsynchronized cross-thread read, same as NodeWorker.MediaSigningKey/StorageRoot —
    /// worst case is one extra frame (~200ms at 5fps) of staleness, irrelevant next to a pre/post
    /// roll window measured in seconds to tens of seconds.</summary>
    public bool IsActive => _spanStart is not null;

    /// <summary>UTC timestamp of the most recent frame where motion was present, confirmed span or
    /// not — deliberately not gated on confirmation (unlike Observe's returned span, which
    /// backdates to _motionSince but only once startAfter has elapsed), so anything checked against
    /// this stays on the conservative, over-keep side even for motion too brief to ever become its
    /// own reported span. Monotonically non-decreasing (each Observe call's timestampUtc is later
    /// than the last) — that property is what lets M8 pass 3's recording-mode gating answer "did
    /// motion happen at any point during an interval" from a single comparison, evaluated once
    /// after the interval has fully elapsed: see MotionSession.HasMotionSince.</summary>
    public DateTime? LastMotionAtUtc { get; private set; }

    /// <summary>Feed one frame's result. Returns a completed span the moment motion has been absent
    /// for <paramref name="endAfter"/>-2 continuously — most calls return null.</summary>
    public MotionSpanResult? Observe(DateTime timestampUtc, bool motionPresent, double score)
    {
        if (motionPresent)
        {
            LastMotionAtUtc = timestampUtc;
            _quietSince = null;
            _motionSince ??= timestampUtc;
            _peakScore = Math.Max(_peakScore, score);

            // A span is confirmed to have started once motion has held for startAfter — backdated to
            // when it actually began (_motionSince), not the confirmation instant, so a short
            // debounce doesn't systematically clip the front of every span.
            if (_spanStart is null && timestampUtc - _motionSince.Value >= startAfter)
            {
                _spanStart = _motionSince.Value;
            }
            return null;
        }

        _motionSince = null;
        _quietSince ??= timestampUtc;

        if (_spanStart is not null && timestampUtc - _quietSince.Value >= endAfter)
        {
            var result = new MotionSpanResult(_spanStart.Value, timestampUtc, _peakScore);
            _spanStart = null;
            _peakScore = 0;
            return result;
        }
        return null;
    }

    /// <summary>Snapshot of currently-recent activity, confirmed span or not, without closing
    /// anything — unlike Observe's return value (only non-null the instant a *confirmed* span
    /// closes). Uses whichever run-start is available: a confirmed span's _spanStart if one is
    /// open, otherwise an unconfirmed run's _motionSince if one is still in progress. Only counts
    /// as "recent" if LastMotionAtUtc is within <paramref name="recency"/> of <paramref
    /// name="nowUtc"/> — a zone with no activity at all, or nothing since well before this call,
    /// reports null.
    ///
    /// Confirmation-gating here would create exactly the mismatch this method exists to avoid:
    /// segment retention (MotionSession.HasMotionSince) already keys off raw LastMotionAtUtc, not
    /// confirmation, so a scene with frequent short bursts — each too brief to individually reach
    /// startAfter, but frequent enough to keep LastMotionAtUtc recent — gets correctly retained
    /// (blue) while never producing a single *confirmed* span to report, leaving the timeline
    /// permanently green-less even though Motion mode is visibly doing its job. Reporting on raw
    /// recency instead makes what gets shown match what actually drove the keep decision.
    ///
    /// Still bounded, not a return to "a row every 200ms": NodeWorker only calls this once per
    /// ~15s checkpoint tick, not per frame, and NodeService.RecordMotionSpansAsync upserts by
    /// (CameraId, ZoneId, StartUtc) — a genuinely continuous run still collapses into one growing
    /// row. A rapidly flapping run (start resets every time the run breaks) can produce at most one
    /// new row per 15s tick it's still flapping, not one per frame.</summary>
    public MotionSpanResult? CurrentInProgressSpan(DateTime nowUtc, TimeSpan recency)
    {
        var start = _spanStart ?? _motionSince;
        if (start is null) return null;
        if (LastMotionAtUtc is not { } last || nowUtc - last > recency) return null;
        return new MotionSpanResult(start.Value, nowUtc, _peakScore);
    }

    /// <summary>Closes an in-progress span at <paramref name="nowUtc"/> without waiting out
    /// endAfter — called when the pipeline itself is stopping (camera reassigned, node shutting
    /// down, ffmpeg reconnecting), so a real in-progress span isn't silently dropped rather than
    /// reported with whatever end time is actually known.</summary>
    public MotionSpanResult? Flush(DateTime nowUtc)
    {
        if (_spanStart is null) return null;
        var result = new MotionSpanResult(_spanStart.Value, nowUtc, _peakScore);
        _spanStart = null;
        _peakScore = 0;
        _motionSince = null;
        _quietSince = null;
        return result;
    }
}
