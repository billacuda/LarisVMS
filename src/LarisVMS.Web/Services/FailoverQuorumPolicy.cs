using LarisVMS.Core.Enums;

namespace LarisVMS.Web.Services;

/// <summary>
/// The pure decision at the heart of recording failover (LarisVMS failover plan, phase 3): given the
/// votes that actually came in this cycle for one subject node, does its recording move to the backup,
/// move back, or stay put? Split out of <c>RecordingFailoverService</c> — which owns the I/O (probing,
/// reading heartbeat payloads) and the time-based debounce — so this truth table is unit-testable with
/// no moving parts, the same reasoning as <c>UpdaterLogic</c> / <c>NodeWorker.ShouldDiscardSegment</c>.
///
/// The rule (from the plan's "Tally rule"): a voter that could not be reached this cycle simply does
/// not vote — it drops out of <em>both</em> the numerator and the denominator. Let V be the voters
/// that did respond. Act only when <c>V &gt;= 2</c> and a <em>strict</em> majority agrees: 2-of-2,
/// 2-of-3, 3-of-3 flip the state; a 1–1 tie, or only one reachable voter, changes nothing and the
/// last state holds. Fail-back is the same test inverted (a strict majority must agree the service is
/// back). Maintenance-driven failover is an operator decision and never reaches this policy.
/// </summary>
public static class FailoverQuorumPolicy
{
    /// <summary>One voter's read of the subject node's service this cycle. A <c>/health</c> timeout,
    /// a non-200, or a body that doesn't parse maps to <see cref="Down"/>; a clean 200 + parseable
    /// body maps to <see cref="Up"/>. A voter that didn't respond at all is simply not in the list.</summary>
    public enum Vote { Up, Down }

    public enum Verdict
    {
        /// <summary>Not enough agreement to move in either direction — hold the current state.</summary>
        NoChange,

        /// <summary>A strict majority of responding voters say the node's service is down — its
        /// cameras should move to the backup node.</summary>
        FailOver,

        /// <summary>A strict majority say the service is back — its cameras should return to it
        /// (only relevant when it is currently <see cref="NodeFailoverState.FailedOverAway"/> for
        /// <see cref="NodeFailoverReason.QuorumOffline"/>).</summary>
        FailBack,
    }

    /// <param name="votes">The voters that responded this cycle (non-responders omitted entirely).</param>
    /// <param name="currentState">The subject node's persisted <see cref="NodeFailoverState"/>.</param>
    public static Verdict Evaluate(IReadOnlyCollection<Vote> votes, NodeFailoverState currentState)
    {
        var v = votes.Count;
        if (v < 2) return Verdict.NoChange; // never act on fewer than two voters

        var down = votes.Count(x => x == Vote.Down);
        var up = v - down;

        // FailedOverAway is the only state from which we're looking to come *back*; from Normal (and
        // defensively from HostingFailover — a hosting node that itself goes down must still fail
        // over) we're looking to go *away*.
        if (currentState == NodeFailoverState.FailedOverAway)
            return up * 2 > v ? Verdict.FailBack : Verdict.NoChange;
        return down * 2 > v ? Verdict.FailOver : Verdict.NoChange;
    }
}
