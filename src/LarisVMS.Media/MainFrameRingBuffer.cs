namespace LarisVMS.Media;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 3a: holds a short, bounded window of the Main
/// stream's own recent fMP4 fragments in RAM, fed entirely from RecordingSession's existing
/// LiveFragmentReceived event — no new RTSP session, no change to RecordingSession's ffmpeg
/// arguments. Lets a later, on-demand caller (LarisVMS.Vision.Service, via a node route) fetch a
/// recent Main-stream instant to decode and re-run detection against at full resolution, instead of
/// lazily seeking into an already-written segment file after the fact (the mechanism behind the
/// Segment.DurationMs-vs-real-duration drift bug fixed in v0.161.6 — a workaround for the underlying
/// lazy-seek approach's fragility, not a cure).
///
/// Deliberately not built on a Channel&lt;T&gt; the way LiveViewerHandler's per-viewer push queue is —
/// this needs random-access "what's closest to instant X" reads from a single shared window, not a
/// FIFO one consumer drains. A plain lock is enough: writes are pure in-memory list operations (no
/// I/O), satisfying RecordingSession's own non-blocking-subscriber contract (a slow/blocking handler
/// directly stalls the recording drain loop, since fragment dispatch is a synchronous invoke, not
/// queued internally).
///
/// Restart-safe with no new API on RecordingSession: callers pass the session's own CURRENT
/// LiveInitSegment on every OnFragment call (read fresh, not cached by the caller) rather than this
/// class waiting once via WaitForLiveInitSegmentAsync — that TCS resolves only once per ffmpeg
/// attempt, so a wait-once subscriber would silently keep buffering fragments against a stale,
/// no-longer-matching init segment after every restart. Comparing the reference on every fragment
/// (LiveInitSegment is reassigned to a new array each attempt) detects a restart within one fragment
/// arrival (~1-2s) at zero cost, with no event/API change to RecordingSession at all.
/// </summary>
public sealed class MainFrameRingBuffer(TimeSpan? window = null, long? maxBytes = null)
{
    // ~2MB/s/camera compressed vs ~450MB/s/camera decoded (see the original plan's own sizing) — a
    // high-bitrate 4K camera can never grow this buffer without limit even if the time window alone
    // would otherwise allow it.
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(10);
    private const long DefaultMaxBytes = 32_000_000;

    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly long _maxBytes = maxBytes ?? DefaultMaxBytes;
    private readonly object _lock = new();
    private byte[]? _initSegment;
    private readonly List<(byte[] Bytes, DateTime ArrivalUtc)> _fragments = [];
    private long _totalBytes;

    /// <summary>Call on every LiveFragmentReceived tick, passing the session's own current
    /// LiveInitSegment alongside it. A reference change from what this buffer currently holds means
    /// ffmpeg restarted since the last call — prior fragments (which only decode against the OLD
    /// init segment's codec params) are dropped rather than silently corrupting whatever's captured
    /// next, mirroring RecordingSession's own "replaced, not appended, on restart" rule for
    /// LiveInitSegment itself. currentInitSegment is only ever null if called before
    /// LiveFragmentReceived's own documented precondition (LiveInitSegment already exists) — treated
    /// as a no-op rather than throwing, since a caller wiring this up is expected to subscribe
    /// unconditionally rather than re-deriving that precondition itself. nowUtc is a caller-supplied
    /// timestamp rather than an internal DateTime.UtcNow read, same reasoning MotionHysteresis.Observe
    /// already established in this codebase — makes eviction timing deterministically testable.</summary>
    public void OnFragment(byte[]? currentInitSegment, byte[] fragment, DateTime nowUtc)
    {
        if (currentInitSegment is null) return;

        lock (_lock)
        {
            if (!ReferenceEquals(_initSegment, currentInitSegment))
            {
                _initSegment = currentInitSegment;
                _fragments.Clear();
                _totalBytes = 0;
            }

            _fragments.Add((fragment, nowUtc));
            _totalBytes += fragment.Length;

            var cutoff = nowUtc - _window;
            while (_fragments.Count > 0 && (_fragments[0].ArrivalUtc < cutoff || _totalBytes > _maxBytes))
            {
                _totalBytes -= _fragments[0].Bytes.Length;
                _fragments.RemoveAt(0);
            }
        }
    }

    /// <summary>The init segment plus whichever buffered fragment's arrival is closest to
    /// <paramref name="atUtc"/> — one fragment is enough since each is independently decodable
    /// (frag_keyframe-muxed, confirmed on RecordingSession's own fMP4 flags). Null if nothing has
    /// arrived yet, or the buffer is currently empty (evicted, or mid-restart with no fragment since
    /// the new init segment landed). Uses arrival time as a proxy for presentation time, not an exact
    /// match — the same roughly-±0.5s-class tolerance this codebase already accepts elsewhere for
    /// Sub/Main timing (see the overhaul plan's own "Two simplifications" section for why a rigid
    /// frame-accurate formula was deliberately not built).</summary>
    public (byte[] InitSegment, byte[] Fragment)? TryGet(DateTime atUtc)
    {
        lock (_lock)
        {
            if (_initSegment is null || _fragments.Count == 0) return null;

            var best = _fragments[0];
            foreach (var f in _fragments)
            {
                if (Math.Abs((f.ArrivalUtc - atUtc).TotalMilliseconds) < Math.Abs((best.ArrivalUtc - atUtc).TotalMilliseconds))
                    best = f;
            }

            return (_initSegment, best.Bytes);
        }
    }
}
