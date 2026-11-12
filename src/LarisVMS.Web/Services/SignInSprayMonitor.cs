using System.Net;

namespace LarisVMS.Web.Services;

/// <summary>
/// Notices password guessing that's spread out so no single address trips the per-address limits:
/// one account failing from several networks, or many failures across the server from many networks
/// (one password tried against many accounts). Kept in memory for <see cref="Window"/>; each kind of
/// notice goes out at most once per <see cref="NoticeInterval"/> (per account for the first).
/// </summary>
public sealed class SignInSprayMonitor(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan NoticeInterval = TimeSpan.FromHours(1);

    /// <summary>One account: this many failures from at least <see cref="AccountMinNetworks"/> networks.</summary>
    public const int AccountFailures = 10;
    public const int AccountMinNetworks = 3;

    /// <summary>The whole server: this many failures from at least <see cref="ServerMinNetworks"/> networks.</summary>
    public const int ServerFailures = 50;
    public const int ServerMinNetworks = 10;

    private const int MaxRemembered = 20_000;

    public static SignInSprayMonitor Shared { get; } = new(TimeProvider.System);

    private readonly Lock _lock = new();
    private readonly Queue<(DateTimeOffset At, string Account, string Network)> _failures = new();
    private readonly Dictionary<string, DateTimeOffset> _accountNoticeSent = new(StringComparer.Ordinal);
    private DateTimeOffset _serverNoticeSent = DateTimeOffset.MinValue;

    public abstract record Notice(int Failures, int Networks);
    /// <summary>One account is being guessed from several networks.</summary>
    public sealed record AccountNotice(string Account, int Failures, int Networks) : Notice(Failures, Networks);
    /// <summary>Failures across many accounts from many networks.</summary>
    public sealed record ServerNotice(int Failures, int Networks, int Accounts) : Notice(Failures, Networks);

    /// <summary>Records one failed sign-in. Returns a notice to send, or null.</summary>
    public Notice? RecordFailure(string account, IPAddress? address)
    {
        var now = time.GetUtcNow();
        var key = SignInThrottle.NameKey(account);
        var network = SignInThrottle.AddressKey(address);
        lock (_lock)
        {
            _failures.Enqueue((now, key, network));
            while (_failures.Count > 0 && (now - _failures.Peek().At > Window || _failures.Count > MaxRemembered))
                _failures.Dequeue();

            var forAccount = _failures.Where(f => f.Account == key).ToList();
            var accountNetworks = forAccount.Select(f => f.Network).Distinct().Count();
            if (forAccount.Count >= AccountFailures && accountNetworks >= AccountMinNetworks
                && (!_accountNoticeSent.TryGetValue(key, out var sent) || now - sent > NoticeInterval))
            {
                if (_accountNoticeSent.Count > 1000) _accountNoticeSent.Clear();
                _accountNoticeSent[key] = now;
                return new AccountNotice(account.Trim(), forAccount.Count, accountNetworks);
            }

            var networks = _failures.Select(f => f.Network).Distinct().Count();
            if (_failures.Count >= ServerFailures && networks >= ServerMinNetworks && now - _serverNoticeSent > NoticeInterval)
            {
                _serverNoticeSent = now;
                return new ServerNotice(_failures.Count, networks, _failures.Select(f => f.Account).Distinct().Count());
            }
            return null;
        }
    }
}
