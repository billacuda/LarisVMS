namespace LarisVMS.Web.Services;

/// <summary>
/// Failover plan phase 1: process-wide counters for how much live/playback traffic LarisVMS.Web is
/// still relaying, so the direct-streaming toggle can be A/B'd on a real deployment — with the toggle
/// on Direct these trend toward zero, which (alongside the client <c>/api/media/timing</c> beacon and
/// host CPU/NIC) is the measurement. Singleton; reset only by a process restart.
/// </summary>
public sealed class MediaRelayMetrics
{
    private long _activeLiveRelays;
    private long _liveRelaysStarted;
    private long _playbackSegmentsProxied;
    private long _playbackBytesProxied;
    private readonly DateTime _sinceUtc = DateTime.UtcNow;

    public DateTime SinceUtc => _sinceUtc;
    public long ActiveLiveRelays => Interlocked.Read(ref _activeLiveRelays);
    public long LiveRelaysStarted => Interlocked.Read(ref _liveRelaysStarted);
    public long PlaybackSegmentsProxied => Interlocked.Read(ref _playbackSegmentsProxied);
    public long PlaybackBytesProxied => Interlocked.Read(ref _playbackBytesProxied);

    public void EnterLiveRelay()
    {
        Interlocked.Increment(ref _activeLiveRelays);
        Interlocked.Increment(ref _liveRelaysStarted);
    }

    public void ExitLiveRelay() => Interlocked.Decrement(ref _activeLiveRelays);

    public void RecordPlaybackProxied(long bytes)
    {
        Interlocked.Increment(ref _playbackSegmentsProxied);
        if (bytes > 0) Interlocked.Add(ref _playbackBytesProxied, bytes);
    }
}
