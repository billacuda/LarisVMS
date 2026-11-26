namespace LarisVMS.Core;

/// <summary>
/// How often a node or proxy tries an update it's offered. The server offers it again on every
/// check-in until it's installed, so one that keeps failing would be downloaded every 30 seconds.
/// A different version starts over. Used from one loop; not thread-safe.
/// </summary>
public sealed class UpdateBackoff
{
    private static readonly TimeSpan First = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Longest = TimeSpan.FromHours(1);

    private string? _version;
    private TimeSpan _wait;
    private DateTime _nextUtc;

    /// <summary>Whether to try <paramref name="version"/> now. A true answer counts as a try.</summary>
    public bool ShouldTry(string version, DateTime nowUtc)
    {
        if (version != _version)
        {
            _version = version;
            _wait = First;
        }
        else
        {
            if (nowUtc < _nextUtc) return false;
            _wait = TimeSpan.FromTicks(Math.Min(_wait.Ticks * 2, Longest.Ticks));
        }
        _nextUtc = nowUtc + _wait;
        return true;
    }
}
