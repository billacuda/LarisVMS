using System.Collections.Concurrent;

namespace LarisVMS.Node;

/// <summary>
/// Failover plan phase 5c: an in-memory set of media-token ids (<c>jti</c>) this node has already
/// served, so a captured one-shot Web→Node control token — playback segment, hover thumbnail,
/// snapshot crop, export trigger/download/delete, service restart — can't be replayed within its
/// 30–60 second validity window.
///
/// Only "v2" tokens (issued by a Web tier at 0.190.0 or newer) carry a jti. A v1 token, or the
/// live-view token family (deliberately multi-use — one browser reuses it across the video and
/// overlay sockets), passes a null jti here and is always allowed, exactly as before this existed.
///
/// Bounded purely by how short the tokens live: every entry self-expires and a periodic sweep drops
/// the stragglers, so the set never holds more than about two minutes of control-plane traffic.
/// Process-local — a restart forgets everything, which at worst lets a token captured moments before
/// the restart be used once more before it expires on its own anyway.
/// </summary>
public sealed class SeenTokenCache
{
    // jti -> unix-seconds after which the entry may be swept. The value is not the token's real exp
    // (the node's own MediaToken validator already enforces that); it's just a generous upper bound
    // so the set self-empties — every one of these token families lives 60s or less.
    private readonly ConcurrentDictionary<string, long> _seen = new();
    private long _lastSweepUnix;

    private const long EntryTtlSeconds = 120;
    private const long SweepIntervalSeconds = 30;

    /// <summary>Records a first use and returns true; returns false if this jti has already been seen
    /// (a replay). A null/empty jti is a v1 token with no replay id — always allowed, never stored.</summary>
    public bool TryConsume(string? jti)
    {
        if (string.IsNullOrEmpty(jti)) return true;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MaybeSweep(now);
        return _seen.TryAdd(jti, now + EntryTtlSeconds);
    }

    private void MaybeSweep(long now)
    {
        var last = Interlocked.Read(ref _lastSweepUnix);
        if (now - last < SweepIntervalSeconds) return;
        // One thread wins the right to sweep this cycle; the rest skip it.
        if (Interlocked.CompareExchange(ref _lastSweepUnix, now, last) != last) return;

        foreach (var (key, expiry) in _seen)
            if (expiry <= now) _seen.TryRemove(key, out _);
    }
}
