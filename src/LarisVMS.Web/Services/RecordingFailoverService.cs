using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>
/// Failover plan phase 3: the recording-failover engine. Every ~15s it decides, per node that has a
/// backup relationship, whether that node's cameras should be recorded by its backup (it is down) or
/// by itself (it is up), and writes the verdict to <see cref="LarisVMS.Core.Entities.Node.FailoverState"/>.
/// Nothing else writes that column — <c>NodeService.GetConfigAsync</c> and the media-routing resolver
/// both read it and never probe in the hot path.
///
/// The decision is a **quorum** (<see cref="FailoverQuorumPolicy"/>): this server's own live
/// <c>/health</c> probe, the partner (backup) node's probe carried in its heartbeat, and any assigned
/// media proxy's probe carried in its heartbeat. A voter that hasn't reported a fresh verdict this
/// cycle simply doesn't vote. A sustained majority (held across <c>Failover.FailoverAfterSeconds</c> /
/// <c>Failover.FailbackAfterSeconds</c> to damp flapping) flips the state, audits, and fires the
/// alert rule pipeline. <see cref="LarisVMS.Core.Entities.Node.MaintenanceMode"/> is an operator
/// override that skips the quorum entirely and holds the node failed-over until it is turned off.
/// </summary>
public sealed class RecordingFailoverService(
    IServiceScopeFactory scopeFactory, ILogger<RecordingFailoverService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    // How stale a partner/proxy heartbeat-borne verdict may be and still count as a live vote — ~3
    // missed 30s heartbeats. Central's own probe is always fresh (it runs it on the tick).
    private static readonly TimeSpan VoteFreshness = TimeSpan.FromSeconds(95);

    private readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    // Per-subject-node hysteresis: the verdict currently being "held" and since when. Only a verdict
    // sustained past the configured window is actually applied.
    private readonly Dictionary<Guid, (FailoverQuorumPolicy.Verdict verdict, DateTime sinceUtc)> _held = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recording-failover tick failed — will retry.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsResolver>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var failoverEnabled = await settings.GetAsync("Failover.Enabled", true, ct: ct);
        var failoverAfter = TimeSpan.FromSeconds(await settings.GetAsync("Failover.FailoverAfterSeconds", 90, ct: ct));
        var failbackAfter = TimeSpan.FromSeconds(await settings.GetAsync("Failover.FailbackAfterSeconds", 120, ct: ct));
        var now = DateTime.UtcNow;

        var nodes = await db.Nodes.ToListAsync(ct);

        // Which nodes are the target of a backup relationship at all — Node.BackupNodeId or a
        // per-camera Camera.BackupNodeIdOverride. Only these are worth evaluating / can host.
        var camBackups = await db.Cameras.AsNoTracking()
            .Where(c => c.BackupNodeIdOverride != null && c.NodeId != null)
            .Select(c => new { Primary = c.NodeId!.Value, Backup = c.BackupNodeIdOverride!.Value })
            .ToListAsync(ct);

        // subject -> its resolved backup (default, ignoring per-camera overrides — those are handled
        // in the resolver; here we only need "does anyone back this node up").
        var hasBackup = new HashSet<Guid>(
            nodes.Where(n => n.BackupNodeId is { } b && b != n.Id).Select(n => n.Id));
        foreach (var cb in camBackups) if (cb.Backup != cb.Primary) hasBackup.Add(cb.Primary);

        // Gather this cycle's votes for every subject up-front (central probes in parallel).
        var partnerReports = CollectReports(nodes.Select(n => (n.Id, n.PartnerHealthReportsJson)));
        var proxyNodes = await db.MediaProxies.AsNoTracking()
            .Select(p => new { p.Id, p.NodeHealthReportsJson }).ToListAsync(ct);
        var proxyReports = CollectReports(proxyNodes.Select(p => (p.Id, p.NodeHealthReportsJson)));

        var dirty = false;

        // ── Pass 1: each node's own down/up verdict (maintenance or quorum) ──────────────────────
        foreach (var subject in nodes)
        {
            var wasFailedOver = subject.FailoverState == NodeFailoverState.FailedOverAway;

            // Maintenance is an explicit operator decision — no quorum, no hysteresis.
            if (subject.MaintenanceMode)
            {
                _held.Remove(subject.Id);
                if (subject.FailoverState != NodeFailoverState.FailedOverAway
                    || subject.FailoverReason != NodeFailoverReason.Maintenance)
                {
                    SetFailedOver(subject, NodeFailoverReason.Maintenance, now);
                    dirty = true;
                    await audit.LogAsync("Node.MaintenanceFailover", null, subject.Name, null,
                        $"{subject.Name} entered maintenance — its cameras move to its backup node.", ct);
                    LogFailoverActivated(subject.Id);
                }
                continue;
            }

            // Was failed over *for maintenance* and maintenance is now off → return to quorum control.
            if (wasFailedOver && subject.FailoverReason == NodeFailoverReason.Maintenance)
            {
                ClearFailover(subject);
                _held.Remove(subject.Id);
                dirty = true;
                await audit.LogAsync("Node.MaintenanceCleared", null, subject.Name, null,
                    $"{subject.Name} left maintenance — its cameras return to it.", ct);
                continue;
            }

            if (!failoverEnabled || !hasBackup.Contains(subject.Id))
            {
                // Quorum failover isn't in play for this node — if it was quorum-failed-over and the
                // feature/backup went away, don't strand it.
                if (wasFailedOver && subject.FailoverReason == NodeFailoverReason.QuorumOffline)
                {
                    ClearFailover(subject);
                    dirty = true;
                }
                _held.Remove(subject.Id);
                continue;
            }

            var votes = await GatherVotesAsync(subject, partnerReports, proxyReports, now, ct);
            var verdict = FailoverQuorumPolicy.Evaluate(votes, subject.FailoverState);

            // Hysteresis: only act on a verdict that has been the same across the window.
            if (verdict == FailoverQuorumPolicy.Verdict.NoChange)
            {
                _held.Remove(subject.Id);
                continue;
            }

            if (!_held.TryGetValue(subject.Id, out var held) || held.verdict != verdict)
            {
                _held[subject.Id] = (verdict, now);
                continue;
            }

            var window = verdict == FailoverQuorumPolicy.Verdict.FailOver ? failoverAfter : failbackAfter;
            if (now - held.sinceUtc < window) continue;

            _held.Remove(subject.Id);

            if (verdict == FailoverQuorumPolicy.Verdict.FailOver)
            {
                SetFailedOver(subject, NodeFailoverReason.QuorumOffline, now);
                dirty = true;
                var breakdown = $"{votes.Count(v => v == FailoverQuorumPolicy.Vote.Down)}/{votes.Count} voters down";
                logger.LogWarning("Recording failover ACTIVATED for node {Node} ({NodeId}): {Breakdown}.",
                    subject.Name, subject.Id, breakdown);
                await audit.LogAsync("Node.FailoverStarted", null, subject.Name, null,
                    $"Quorum agreed {subject.Name}'s service is down ({breakdown}) — its cameras move to its backup node.", ct);
                LogFailoverActivated(subject.Id);
            }
            else // FailBack
            {
                ClearFailover(subject);
                dirty = true;
                var breakdown = $"{votes.Count(v => v == FailoverQuorumPolicy.Vote.Up)}/{votes.Count} voters up";
                logger.LogInformation("Recording failback for node {Node} ({NodeId}): {Breakdown}.",
                    subject.Name, subject.Id, breakdown);
                await audit.LogAsync("Node.FailbackCompleted", null, subject.Name, null,
                    $"Quorum agreed {subject.Name}'s service is back ({breakdown}) — its cameras return to it.", ct);
            }
        }

        // ── Pass 2: HostingFailover for any healthy node currently carrying someone else's cameras ──
        var failedOverIds = nodes.Where(n => n.FailoverState == NodeFailoverState.FailedOverAway)
            .Select(n => n.Id).ToHashSet();
        var hostingIds = new HashSet<Guid>();
        foreach (var down in nodes.Where(n => failedOverIds.Contains(n.Id)))
        {
            // default backup
            if (down.BackupNodeId is { } b && !failedOverIds.Contains(b)) hostingIds.Add(b);
        }
        foreach (var cb in camBackups.Where(cb => failedOverIds.Contains(cb.Primary) && !failedOverIds.Contains(cb.Backup)))
            hostingIds.Add(cb.Backup);

        foreach (var n in nodes)
        {
            if (n.FailoverState == NodeFailoverState.FailedOverAway) continue;
            var target = hostingIds.Contains(n.Id) ? NodeFailoverState.HostingFailover : NodeFailoverState.Normal;
            if (n.FailoverState != target)
            {
                n.FailoverState = target;
                n.FailoverSinceUtc = target == NodeFailoverState.HostingFailover ? (n.FailoverSinceUtc ?? now) : null;
                n.FailoverReason = null;
                dirty = true;
            }
        }

        if (dirty) await db.SaveChangesAsync(ct);
    }

    private static void SetFailedOver(Core.Entities.Node n, NodeFailoverReason reason, DateTime now)
    {
        if (n.FailoverState != NodeFailoverState.FailedOverAway) n.FailoverSinceUtc = now;
        n.FailoverState = NodeFailoverState.FailedOverAway;
        n.FailoverReason = reason;
    }

    private static void ClearFailover(Core.Entities.Node n)
    {
        n.FailoverState = NodeFailoverState.Normal;
        n.FailoverReason = null;
        n.FailoverSinceUtc = null;
    }

    /// <summary>The three voters for one subject node — central's live probe plus the freshest
    /// partner and proxy verdicts. Only voters that actually produced a verdict this cycle are
    /// returned; the policy never acts on fewer than two.</summary>
    private async Task<List<FailoverQuorumPolicy.Vote>> GatherVotesAsync(
        Core.Entities.Node subject,
        IReadOnlyDictionary<Guid, List<(bool running, DateTime at)>> partnerReports,
        IReadOnlyDictionary<Guid, List<(bool running, DateTime at)>> proxyReports,
        DateTime now, CancellationToken ct)
    {
        var votes = new List<FailoverQuorumPolicy.Vote>();

        // 1. Central's own probe (always a live vote when the node has a known address).
        if (!string.IsNullOrWhiteSpace(subject.LastIpAddress) && subject.LivePort is > 0)
            votes.Add(await ProbeCentralAsync(subject.LastIpAddress, subject.LivePort.Value, ct));

        // 2. The freshest partner-node verdict about this subject.
        if (Freshest(partnerReports, subject.Id, now) is { } partner)
            votes.Add(partner ? FailoverQuorumPolicy.Vote.Up : FailoverQuorumPolicy.Vote.Down);

        // 3. The freshest assigned-proxy verdict about this subject.
        if (Freshest(proxyReports, subject.Id, now) is { } proxy)
            votes.Add(proxy ? FailoverQuorumPolicy.Vote.Up : FailoverQuorumPolicy.Vote.Down);

        return votes;
    }

    private static bool? Freshest(IReadOnlyDictionary<Guid, List<(bool running, DateTime at)>> reports, Guid subjectId, DateTime now)
    {
        if (!reports.TryGetValue(subjectId, out var list) || list.Count == 0) return null;
        var newest = list.MaxBy(r => r.at);
        return now - newest.at <= VoteFreshness ? newest.running : null;
    }

    private async Task<FailoverQuorumPolicy.Vote> ProbeCentralAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var response = await _probeHttp.GetAsync(
                $"http://{host}:{port}/health", HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode) return FailoverQuorumPolicy.Vote.Down;
            var body = await response.Content.ReadFromJsonAsync<NodeHealthDto>(ct);
            return body is not null ? FailoverQuorumPolicy.Vote.Up : FailoverQuorumPolicy.Vote.Down;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return FailoverQuorumPolicy.Vote.Down;
        }
    }

    /// <summary>Flattens a set of voters' stored <see cref="NodePartnerHealthReport"/> JSON blobs into
    /// <c>subjectNodeId -&gt; [(running, checkedAt)]</c>.</summary>
    private static Dictionary<Guid, List<(bool running, DateTime at)>> CollectReports(
        IEnumerable<(Guid voterId, string? json)> voters)
    {
        var map = new Dictionary<Guid, List<(bool, DateTime)>>();
        foreach (var (_, json) in voters)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            List<NodePartnerHealthReport>? reports;
            try { reports = System.Text.Json.JsonSerializer.Deserialize<List<NodePartnerHealthReport>>(json); }
            catch (System.Text.Json.JsonException) { continue; }
            if (reports is null) continue;

            foreach (var r in reports)
            {
                if (!map.TryGetValue(r.NodeId, out var list)) map[r.NodeId] = list = [];
                list.Add((r.ServiceRunning, r.CheckedAtUtc));
            }
        }
        return map;
    }

    /// <summary>Nudges the alert pipeline: any <c>NodeFailoverActivated</c> rule for this node is
    /// picked up by <see cref="AlertEvaluatorService"/>'s own next sweep — this just logs the
    /// activation so the sweep and the audit trail line up in time.</summary>
    private void LogFailoverActivated(Guid nodeId)
    {
        logger.LogInformation("Node {NodeId} is now FailedOverAway — any NodeFailoverActivated alert rule will fire on the next evaluation sweep.", nodeId);
    }
}
