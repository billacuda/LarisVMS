using System.Collections.Concurrent;
using System.Threading.Channels;

namespace LarisVMS.Web.Services;

/// <summary>
/// The on-premises notification transport: every open LarisVMS tab holds a Server-Sent Events stream
/// (GET /api/notifications/stream), and this singleton hands each user's notifications to all of that
/// user's open streams. Nothing leaves the network, and nothing is stored — a user with no tab open
/// simply doesn't get it through this transport (Web Push or Firebase cover a closed browser).
/// </summary>
public sealed class NotificationHub
{
    private sealed record Stream(long Opened, Channel<string> Channel);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Stream>> _streams = new();
    private long _opened;

    /// <summary>Open notification streams one person may hold (each open tab holds one).</summary>
    public const int MaxStreamsPerUser = 10;

    /// <summary>Opens one stream for a user. Dispose the result when the request ends. Past
    /// <see cref="MaxStreamsPerUser"/>, the person's oldest streams are closed rather than this one
    /// refused: every page load opens a new stream, and the one from the page just left can linger
    /// until the server notices it's gone, so the newest is the one that matters.</summary>
    public Subscription Subscribe(string userId)
    {
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        var userStreams = _streams.GetOrAdd(userId, _ => new());
        userStreams.TryAdd(id, new Stream(Interlocked.Increment(ref _opened), channel));
        foreach (var oldest in userStreams.OrderBy(kv => kv.Value.Opened).Take(Math.Max(0, userStreams.Count - MaxStreamsPerUser)).ToList())
            if (userStreams.TryRemove(oldest.Key, out var closed)) closed.Channel.Writer.TryComplete();
        return new Subscription(channel.Reader, () =>
        {
            if (_streams.TryGetValue(userId, out var streams))
            {
                streams.TryRemove(id, out _);
                if (streams.IsEmpty) _streams.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Stream>>(userId, streams));
            }
            channel.Writer.TryComplete();
        });
    }

    /// <summary>Queues a JSON payload for every open stream of the user. Returns how many tabs got it.</summary>
    public int Publish(string userId, string json)
    {
        if (!_streams.TryGetValue(userId, out var userStreams)) return 0;
        return userStreams.Values.Count(stream => stream.Channel.Writer.TryWrite(json));
    }

    public sealed class Subscription(ChannelReader<string> reader, Action onDispose) : IDisposable
    {
        public ChannelReader<string> Reader { get; } = reader;
        public void Dispose() => onDispose();
    }
}
