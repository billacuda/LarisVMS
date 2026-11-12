using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Security;

/// <summary>
/// The audit log's hash chain. Each entry's <see cref="AuditLog.Hash"/> is an HMAC-SHA256 over the
/// previous entry's hash and its own fields, keyed with <see cref="AuditChainState.Key"/>. The key
/// is encrypted with the server's data-protection keys, so someone who can only reach the database
/// can't recompute a hash after changing, adding, or removing an entry. Retention removes entries
/// from the oldest end and records where it stopped (<see cref="AuditChainState.PrunedThroughHash"/>).
///
/// A small record kept outside the database (<see cref="AuditChainAnchor"/>) remembers the key's
/// fingerprint and where the chain started, so swapping the key or removing the state row and the
/// hashes doesn't pass as a fresh, clean chain.
///
/// What it can't catch: someone with the server's own keys, or the whole database (state row
/// included) rolled back to an earlier copy. Forwarding to a syslog server covers those.
/// </summary>
public static class AuditChain
{
    public const int HashLength = 64;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Times are kept to the microsecond: MariaDB's datetime(6) drops the last digit of a
    /// .NET tick, and the hash has to match what reads back.</summary>
    public static DateTime NormalizeTime(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

    public static string ComputeHash(byte[] key, string previousHash, AuditLog entry)
    {
        var text = new StringBuilder("entry/v1");
        Append(text, previousHash);
        Append(text, entry.Action);
        Append(text, entry.UserId);
        Append(text, entry.UserName);
        Append(text, entry.IpAddress);
        Append(text, entry.Details);
        Append(text, entry.OccurredAt.Ticks.ToString(CultureInfo.InvariantCulture));
        return Hmac(key, text.ToString());
    }

    public static string ComputeSeal(byte[] key, string lastHash, string? prunedThroughHash)
    {
        var text = new StringBuilder("state/v1");
        Append(text, lastHash);
        Append(text, prunedThroughHash);
        return Hmac(key, text.ToString());
    }

    /// <summary>Identifies a key without revealing it, for <see cref="AuditChainAnchor"/>.</summary>
    public static string KeyId(byte[] key) => Hmac(key, "anchor/v1")[..32];

    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && a.Length == b.Length
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    // Length-prefixed, with null distinct from "", so no two different entries read the same.
    private static void Append(StringBuilder text, string? value)
    {
        text.Append('|');
        if (value is null) { text.Append('-'); return; }
        text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    private static string Hmac(byte[] key, string text) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));
}

public enum AuditChainProblem
{
    None,
    /// <summary>An entry's fields don't match its hash.</summary>
    EntryChanged,
    /// <summary>An entry doesn't follow the one before it: entries between them were removed.</summary>
    EntryMissing,
    /// <summary>An entry without a hash after the chain started: added directly in the database.</summary>
    EntryUnsealed,
    /// <summary>The oldest entries were removed other than by retention.</summary>
    OldestRemoved,
    /// <summary>The newest entries were removed.</summary>
    NewestRemoved,
    /// <summary>The chain's state row was changed or removed.</summary>
    StateChanged,
}

/// <summary>Kept outside the database (encrypted, in the server's data folder): the chain's key
/// fingerprint (<see cref="AuditChain.KeyId"/>), and the last entry written before the chain
/// started; every later entry must be sealed. Written once, never replaced.</summary>
public sealed record AuditChainAnchor(string KeyId, long SealedAfterId);

public sealed record AuditChainReport(int Sealed, int Unsealed, AuditChainProblem Problem, long? AtId, DateTime CheckedUtc)
{
    public bool Ok => Problem == AuditChainProblem.None;
}

/// <summary>Walks the log oldest first. Feed every entry to <see cref="Add"/> in id order, then call
/// <see cref="Finish"/>. Stops recording at the first problem.</summary>
public sealed class AuditChainVerifier
{
    private readonly byte[]? _key;
    private readonly AuditChainState? _state;
    private readonly string _expectedFirst;
    private string _previous;
    private bool _started;
    private int _sealed, _unsealed;
    private long? _lastId;
    private AuditChainProblem _problem;
    private long? _at;

    private readonly AuditChainAnchor? _anchor;

    public AuditChainVerifier(byte[]? key, AuditChainState? state, AuditChainAnchor? anchor = null)
    {
        _key = key;
        _state = state;
        _anchor = anchor;
        _expectedFirst = state?.PrunedThroughHash ?? "";
        _previous = _expectedFirst;
        if (state is not null && (key is null || !AuditChain.Same(state.Seal, AuditChain.ComputeSeal(key, state.LastHash, state.PrunedThroughHash))))
            _problem = AuditChainProblem.StateChanged;
        // The chain was started on this server: its state row must still be there, with the same key.
        else if (anchor is not null && (state is null || key is null || !AuditChain.Same(anchor.KeyId, AuditChain.KeyId(key))))
            _problem = AuditChainProblem.StateChanged;
    }

    public void Add(AuditLog entry)
    {
        if (_problem != AuditChainProblem.None) return;
        _lastId = entry.Id;

        if (entry.Hash is null)
        {
            if (_started || (_anchor is not null && entry.Id > _anchor.SealedAfterId)) Fail(AuditChainProblem.EntryUnsealed, entry.Id);
            else _unsealed++;
            return;
        }
        if (_key is null || _state is null) { Fail(AuditChainProblem.StateChanged, entry.Id); return; }

        if (!AuditChain.Same(entry.PreviousHash ?? "", _previous))
        {
            Fail(_started ? AuditChainProblem.EntryMissing : AuditChainProblem.OldestRemoved, entry.Id);
            return;
        }
        _started = true;
        if (!AuditChain.Same(entry.Hash, AuditChain.ComputeHash(_key, entry.PreviousHash ?? "", entry)))
        {
            Fail(AuditChainProblem.EntryChanged, entry.Id);
            return;
        }
        _previous = entry.Hash;
        _sealed++;
    }

    public AuditChainReport Finish(DateTime checkedUtc)
    {
        if (_problem == AuditChainProblem.None && _state is not null && !AuditChain.Same(_previous, _state.LastHash))
            Fail(AuditChainProblem.NewestRemoved, _lastId);
        return new AuditChainReport(_sealed, _unsealed, _problem, _at, checkedUtc);
    }

    private void Fail(AuditChainProblem problem, long? at)
    {
        _problem = problem;
        _at = at;
    }
}
