using System.Collections.Concurrent;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>
/// Failover plan phase 3: the hand-off between <see cref="PartnerProbeService"/> (which does the
/// <c>/health</c> probing on its own ~15s cadence) and <see cref="NodeWorker"/> (which folds the
/// latest verdicts into its 30s heartbeat). A singleton so both see the same state; deliberately tiny
/// and lock-free.
/// </summary>
public sealed class PartnerHealthTracker
{
    private volatile IReadOnlyList<NodePartnerProbeDto> _partners = [];
    private readonly ConcurrentDictionary<Guid, NodePartnerHealthReport> _reports = new();

    /// <summary>The partner nodes this node is currently asked to probe — replaced wholesale by
    /// <see cref="NodeWorker"/> each reconcile from <see cref="NodeConfigResponse.PartnersToProbe"/>.</summary>
    public IReadOnlyList<NodePartnerProbeDto> Partners => _partners;

    public void SetPartners(IReadOnlyList<NodePartnerProbeDto>? partners)
    {
        var next = partners ?? [];
        _partners = next;

        // Forget verdicts for partners we're no longer responsible for, so a stale "down" for a node
        // this one stopped backing up can't linger into a heartbeat.
        var keep = next.Select(p => p.NodeId).ToHashSet();
        foreach (var gone in _reports.Keys.Where(k => !keep.Contains(k)).ToList())
            _reports.TryRemove(gone, out _);
    }

    public void Record(NodePartnerHealthReport report) => _reports[report.NodeId] = report;

    /// <summary>The most recent verdict per partner — what the next heartbeat carries. Only partners
    /// actually probed this cycle appear; a partner not yet reached simply isn't a vote.</summary>
    public List<NodePartnerHealthReport> CurrentReports() => _reports.Values.ToList();
}
