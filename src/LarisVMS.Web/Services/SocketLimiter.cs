using System.Collections.Concurrent;
using System.Security.Claims;

namespace LarisVMS.Web.Services;

/// <summary>
/// Caps how many live WebSockets (video, talk, overlays) one signed-in person holds through this
/// server at once, so one account can't tie up the relay. Generous: a big video wall is a few dozen
/// tiles, each with a video socket and an overlay socket.
/// </summary>
public static class SocketLimiter
{
    public const int MaxPerUser = 200;

    private static readonly ConcurrentDictionary<string, int> Open = new();

    /// <summary>A slot to hold for the socket's lifetime, or null when the person is at the cap.</summary>
    public static IDisposable? TryEnter(HttpContext context)
    {
        var key = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "";
        var count = Open.AddOrUpdate(key, 1, (_, n) => n + 1);
        if (count <= MaxPerUser) return new Slot(key);
        Release(key);
        return null;
    }

    private static void Release(string key)
    {
        var remaining = Open.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));
        if (remaining == 0) Open.TryRemove(new KeyValuePair<string, int>(key, 0));
    }

    private sealed class Slot(string key) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) Release(key);
        }
    }
}
