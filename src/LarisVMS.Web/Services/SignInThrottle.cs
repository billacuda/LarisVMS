using System.Collections.Concurrent;
using System.Net;

namespace LarisVMS.Web.Services;

/// <summary>
/// Slows password guessing per account <em>and</em> address: after <see cref="MaxFailures"/> wrong
/// tries at one sign-in name from one address within <see cref="Window"/>, that address is turned
/// away for that name until the window is up. Someone guessing from one place is stopped without
/// locking the real person out everywhere, which a plain account lockout lets anyone do to anyone.
/// Names are counted whether or not an account has them, so being turned away says nothing about
/// whether one exists. The account lockout stays as a much higher cap for guessing spread across
/// many addresses (and never applies to Super Admins at sign-in). In memory: a restart forgets.
/// </summary>
public sealed class SignInThrottle(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int MaxTracked = 20_000;

    private sealed class Entry
    {
        public int Count;
        public DateTimeOffset WindowStart;
        public DateTimeOffset BlockedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public SignInThrottle() : this(TimeProvider.System) { }

    /// <summary>How long this address must wait before trying this name again; null when it may.</summary>
    public TimeSpan? BlockedFor(string name, IPAddress? address)
    {
        if (!_entries.TryGetValue(Key(name, address), out var entry)) return null;
        lock (entry)
        {
            var left = entry.BlockedUntil - time.GetUtcNow();
            return left > TimeSpan.Zero ? left : null;
        }
    }

    public void RecordFailure(string name, IPAddress? address)
    {
        var now = time.GetUtcNow();
        if (_entries.Count >= MaxTracked) Prune(now);
        var entry = _entries.GetOrAdd(Key(name, address), _ => new Entry { WindowStart = now });
        lock (entry)
        {
            if (now - entry.WindowStart > Window)
            {
                entry.WindowStart = now;
                entry.Count = 0;
            }
            if (++entry.Count >= MaxFailures) entry.BlockedUntil = now + Window;
        }
    }

    public void RecordSuccess(string name, IPAddress? address) => _entries.TryRemove(Key(name, address), out _);

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
            if (now - entry.WindowStart > Window && entry.BlockedUntil <= now) _entries.TryRemove(key, out _);
        // Still full (an attack spraying names): forget the oldest half rather than grow without end.
        if (_entries.Count >= MaxTracked)
            foreach (var key in _entries.OrderBy(e => e.Value.WindowStart).Take(MaxTracked / 2).Select(e => e.Key))
                _entries.TryRemove(key, out _);
    }

    private static string Key(string name, IPAddress? address) => NameKey(name) + "|" + AddressKey(address);

    /// <summary>One key per account: "CORP\bob", "other\bob", and "bob" are the same Active Directory
    /// sign-in (the domain part is dropped there too), so each spelling doesn't get its own tries.</summary>
    internal static string NameKey(string name)
    {
        var trimmed = name.Trim();
        var slash = trimmed.LastIndexOf('\\');
        return (slash >= 0 ? trimmed[(slash + 1)..] : trimmed).ToUpperInvariant();
    }

    /// <summary>One key per client: an IPv4 address (IPv4-mapped IPv6 included), or an IPv6 address's
    /// /64 network, since one household or attacker usually has a whole /64 to rotate through.</summary>
    public static string AddressKey(IPAddress? address)
    {
        if (address is null) return "";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
