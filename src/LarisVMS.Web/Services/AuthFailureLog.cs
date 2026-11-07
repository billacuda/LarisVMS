using System.Collections.Concurrent;
using System.Net;

namespace LarisVMS.Web.Services;

/// <summary>
/// Refusals that otherwise left no trace: a bad node bearer token, an invalid API key, a request from
/// outside an IP allow list. Each is logged as a warning the first time in a minute for that kind and
/// address (and again if it keeps going), so attempts are visible without a flood filling the log.
/// The count lets a caller that keeps guessing be slowed down.
/// </summary>
public static class AuthFailureLog
{
    private const long WindowMs = 60_000;
    private const int RepeatedThreshold = 20;
    private const int MaxTracked = 10_000;

    private sealed class Window
    {
        public long Start;
        public int Count;
    }

    private static readonly ConcurrentDictionary<(string Kind, string Address), Window> Windows = new();

    /// <summary>Records one refusal; returns how many this address has had of this kind in the current
    /// minute, this one included.</summary>
    public static int Record(ILogger logger, string kind, IPAddress? remoteIp, string? detail = null)
    {
        var address = remoteIp is null ? "unknown" : (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString();
        // Many distinct addresses only cost a reset of the counts, never unbounded memory.
        if (Windows.Count > MaxTracked) Windows.Clear();

        var now = Environment.TickCount64;
        var window = Windows.GetOrAdd((kind, address), _ => new Window { Start = now });
        int count;
        lock (window)
        {
            if (now - window.Start >= WindowMs) { window.Start = now; window.Count = 0; }
            count = ++window.Count;
        }

        if (count == 1)
            logger.LogWarning("{Kind} refused for {Address}{Detail}.", kind, address, detail is null ? "" : $" ({detail})");
        else if (count == RepeatedThreshold)
            logger.LogWarning("{Kind}: {Count} refusals for {Address} within a minute.", kind, count, address);
        return count;
    }

    /// <summary>For tests.</summary>
    internal static void Reset() => Windows.Clear();
}
