using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LarisVMS.Relay;

/// <summary>Caps how many connections one address may hold open. The proxy relays a request to the node
/// before the node has looked at its ticket, so without a cap anyone who can reach it could hold any
/// number of connections to both.</summary>
public sealed class ConnectionLimiter(int maxPerAddress)
{
    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.Ordinal);

    /// <summary>A place for one connection, to dispose when it ends; null when the address has none left.</summary>
    public IDisposable? TryEnter(IPAddress? address)
    {
        var key = Key(address);
        if (_open.AddOrUpdate(key, 1, (_, n) => n + 1) <= maxPerAddress) return new Slot(this, key);
        Release(key);
        return null;
    }

    private void Release(string key)
    {
        if (_open.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1)) == 0)
            _open.TryRemove(new KeyValuePair<string, int>(key, 0));
    }

    // One IPv6 machine usually has its whole /64 to pick addresses from.
    private static string Key(IPAddress? address)
    {
        if (address is null) return "";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString();
    }

    private sealed class Slot(ConnectionLimiter owner, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(key);
        }
    }
}
