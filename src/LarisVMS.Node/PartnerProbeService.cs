using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;

namespace LarisVMS.Node;

/// <summary>
/// Failover plan phase 3: probes the recorder nodes this node is the backup for, so central's
/// <c>RecordingFailoverService</c> has a second (LAN-local) opinion on whether a partner's service is
/// actually running before it moves that partner's cameras here. Deliberately a real service check —
/// GET <c>/health</c> with a short timeout, a body that must parse — not a ping: a lingering socket
/// or a load balancer answering for a dead process has to read as <em>down</em>.
///
/// Its own ~15s cadence, independent of <see cref="NodeWorker"/>'s 30s reconcile (so a probe result
/// is at most ~15s stale when the heartbeat picks it up), same "sibling of StorageManager's periodic
/// sweep" shape as <c>CertWatcherService</c>. Results land in the shared <see cref="PartnerHealthTracker"/>.
/// </summary>
public sealed class PartnerProbeService(PartnerHealthTracker tracker, ILogger<PartnerProbeService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Plain HTTP — the node's /health lives on its LAN livePort, not the optional client HTTPS
        // endpoint. No auth: /health is unauthenticated by design (see the node's own route comment).
        using var http = new HttpClient { Timeout = ProbeTimeout };

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var partner in tracker.Partners)
            {
                if (stoppingToken.IsCancellationRequested) break;
                tracker.Record(await ProbeAsync(http, partner, stoppingToken));
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<NodePartnerHealthReport> ProbeAsync(HttpClient http, NodePartnerProbeDto partner, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        try
        {
            using var response = await http.GetAsync(
                $"http://{partner.Host}:{partner.Port}/health", HttpCompletionOption.ResponseContentRead, ct);

            if (!response.IsSuccessStatusCode)
                return new NodePartnerHealthReport(partner.NodeId, false, now, $"HTTP {(int)response.StatusCode}");

            // A 200 whose body doesn't deserialize to the expected shape is still "not the recorder
            // service" — something else is answering on that port.
            var body = await response.Content.ReadFromJsonAsync<NodeHealthDto>(ct);
            return body is null
                ? new NodePartnerHealthReport(partner.NodeId, false, now, "unparseable /health body")
                : new NodePartnerHealthReport(partner.NodeId, true, now, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Partner /health probe of node {NodeId} at {Host}:{Port} failed.",
                partner.NodeId, partner.Host, partner.Port);
            return new NodePartnerHealthReport(partner.NodeId, false, now,
                ex is TaskCanceledException ? "timeout" : ex.GetType().Name);
        }
    }
}
