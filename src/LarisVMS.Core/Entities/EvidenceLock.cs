namespace LarisVMS.Core.Entities;

/// <summary>
/// A stretch of one camera's footage that retention must not delete — kept as evidence until
/// <see cref="ExpiresAtUtc"/> (or indefinitely when that's null). Every deletion path honours it: the
/// node's own sweeps (it receives the protected ranges in its config), the server's expired-segment
/// list, and Web's bookmark/motion-span retention. See <see cref="EvidenceLockRules"/> for exactly when
/// a lock protects footage.
///
/// Removal doesn't end protection straight away: a removed lock keeps protecting for
/// <see cref="EvidenceLockRules.RemovalGrace"/> in case it was removed by mistake (it can be restored
/// in that window). A lock that simply reaches its expiry has no grace period.
/// </summary>
public class EvidenceLock
{
    public Guid Id { get; set; }
    public Guid CameraId { get; set; }

    /// <summary>The footage protected, in UTC.</summary>
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>Null = indefinite.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    public string CreatedByUserId { get; set; } = string.Empty;
    public string? CreatedByUserName { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? RemovedAtUtc { get; set; }
    public string? RemovedByUserId { get; set; }
    public string? RemovedByUserName { get; set; }
    public string? RemovalReason { get; set; }

    /// <summary>Stamped by EvidenceLockSweepService when the lock reaches its expiry (for the audit
    /// entry and the "Expired" status) — protection itself is decided from ExpiresAtUtc.</summary>
    public DateTime? ExpiredAtUtc { get; set; }

    /// <summary>Stamped by the sweep when a removed lock's grace period ends, so that's audited once.</summary>
    public DateTime? GraceEndedAtUtc { get; set; }

    /// <summary>Stamped by the alert evaluator once the "expiring soon" notification has gone out.</summary>
    public DateTime? ExpiryWarnedAtUtc { get; set; }

    /// <summary>Stamped by the alert evaluator once the "lock removed" notification has gone out.
    /// Cleared again when the removal is undone, so a later removal notifies too.</summary>
    public DateTime? RemovalNotifiedAtUtc { get; set; }

    public Camera Camera { get; set; } = null!;
}

public enum EvidenceLockStatus { Active, RemovedInGrace, Expired, Released }

/// <summary>When an evidence lock protects footage — one place, shared by the server queries, the
/// sweep, and the UI.</summary>
public static class EvidenceLockRules
{
    /// <summary>How long a removed lock keeps protecting its footage, in case it was removed by mistake.</summary>
    public static readonly TimeSpan RemovalGrace = TimeSpan.FromHours(24);

    /// <summary>Locks longer than this get the storage warning.</summary>
    public static readonly TimeSpan LongLockWarningThreshold = TimeSpan.FromDays(30);

    /// <summary>The most footage one lock may cover.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(7);

    public static bool IsProtecting(EvidenceLock l, DateTime nowUtc) => IsProtecting(l.ExpiresAtUtc, l.RemovedAtUtc, nowUtc);

    public static bool IsProtecting(DateTime? expiresAtUtc, DateTime? removedAtUtc, DateTime nowUtc)
        => removedAtUtc is { } removed
            ? removed + RemovalGrace > nowUtc
            : expiresAtUtc is not { } expires || expires > nowUtc;

    /// <summary>When protection ends, or null for never (an active indefinite lock).</summary>
    public static DateTime? ProtectedUntil(EvidenceLock l)
        => l.RemovedAtUtc is { } removed ? removed + RemovalGrace : l.ExpiresAtUtc;

    public static EvidenceLockStatus StatusOf(EvidenceLock l, DateTime nowUtc)
    {
        if (l.RemovedAtUtc is { } removed) return removed + RemovalGrace > nowUtc ? EvidenceLockStatus.RemovedInGrace : EvidenceLockStatus.Released;
        return l.ExpiresAtUtc is { } expires && expires <= nowUtc ? EvidenceLockStatus.Expired : EvidenceLockStatus.Active;
    }

    /// <summary>The lock lengths offered in the UI. "custom" takes a date; "indefinite" has no expiry.</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> Durations = new Dictionary<string, TimeSpan>
    {
        ["1d"] = TimeSpan.FromDays(1),
        ["3d"] = TimeSpan.FromDays(3),
        ["7d"] = TimeSpan.FromDays(7),
        ["14d"] = TimeSpan.FromDays(14),
        ["30d"] = TimeSpan.FromDays(30),
    };
}
