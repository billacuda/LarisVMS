using System.Collections.Concurrent;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Tracks long-lived per-user connections (the live-view WebSocket relays) so disabling a user can
/// close them at once. The cookie check in OnValidatePrincipal only runs when a request starts, and a
/// relay is a single request that can stay open for hours.
/// </summary>
public sealed class UserConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Lease, byte>> _byUser = new(StringComparer.Ordinal);

    /// <summary>Returns a lease whose Token cancels when <paramref name="requestAborted"/> does or when
    /// the user is revoked. Dispose it when the connection ends.</summary>
    public Lease Register(string? userId, CancellationToken requestAborted)
    {
        var lease = new Lease(this, userId, CancellationTokenSource.CreateLinkedTokenSource(requestAborted));
        if (userId is not null)
            _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<Lease, byte>())[lease] = 0;
        return lease;
    }

    /// <summary>Cancels every open connection for the user. Returns how many were open.</summary>
    public int RevokeAll(string userId)
    {
        if (!_byUser.TryRemove(userId, out var leases)) return 0;
        foreach (var lease in leases.Keys) lease.Cancel();
        return leases.Count;
    }

    private void Remove(Lease lease)
    {
        if (lease.UserId is null || !_byUser.TryGetValue(lease.UserId, out var leases)) return;
        leases.TryRemove(lease, out _);
        if (leases.IsEmpty) _byUser.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Lease, byte>>(lease.UserId, leases));
    }

    public sealed class Lease : IDisposable
    {
        private readonly UserConnectionRegistry _owner;
        private readonly CancellationTokenSource _cts;

        internal Lease(UserConnectionRegistry owner, string? userId, CancellationTokenSource cts)
        {
            _owner = owner;
            _cts = cts;
            UserId = userId;
        }

        public string? UserId { get; }
        public CancellationToken Token => _cts.Token;

        internal void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* connection already ended */ }
        }

        public void Dispose()
        {
            _owner.Remove(this);
            _cts.Dispose();
        }
    }
}
