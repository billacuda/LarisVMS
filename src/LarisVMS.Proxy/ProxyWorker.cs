using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;
using LarisVMS.Relay;

namespace LarisVMS.Proxy;

/// <summary>
/// Failover plan phase 2: keeps the relay's routing table current. Every 30 seconds it fetches
/// <c>/api/proxies/config</c> (the camera→node address map), persists it so a restart during an
/// outage can still serve, and heartbeats — echoing the phase-5a rolling nonce and reporting what its
/// own HTTPS endpoint is doing. Mirrors LarisVMS.Node's NodeWorker in shape.
/// </summary>
public sealed class ProxyWorker(ProxyApiClient api, ProxyConfig registration, ProxyEndpointConfig endpoint,
    CertHolder? certHolder, ProxyUpdateService updateService, ILogger<ProxyWorker> logger) : BackgroundService
{
    private ProxyConfig _registration = registration;
    private volatile IReadOnlyDictionary<Guid, NodeTarget> _routes =
        BuildRoutes(registration.CachedConfig?.Nodes);
    private volatile string? _webOrigin = registration.CachedConfig?.WebOrigin;

    // Failover plan phase 3: /health probe of the nodes this proxy serves — a short-timeout plain
    // HTTP GET, the same real-service-check semantics PartnerProbeService uses.
    private readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    public readonly record struct NodeTarget(Guid NodeId, string Host, int Port);

    /// <summary>Resolve an incoming <c>/live/{cameraId}</c> or <c>/playback-segment/{cameraId}</c> to
    /// a plain-HTTP node address. Null when this proxy isn't configured to serve that camera.</summary>
    public NodeTarget? Resolve(Guid cameraId) => _routes.TryGetValue(cameraId, out var t) ? t : null;

    /// <summary>The public web origin to send as <c>Access-Control-Allow-Origin</c>, or null to
    /// reflect the request Origin.</summary>
    public string? WebOrigin => _webOrigin;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var config = await api.GetConfigAsync(stoppingToken);
                _routes = BuildRoutes(config.Nodes);
                _webOrigin = config.WebOrigin;
                _registration = _registration with { CachedConfig = config };
                TrySave();

                var nodeHealthReports = await ProbeAssignedNodesAsync(config.NodesToProbe, stoppingToken);

                var heartbeat = await api.HeartbeatAsync(new ProxyHeartbeatRequest(
                    ProxyVersion.Current, DateTime.UtcNow, _registration.CheckInNonce,
                    certHolder is { Current: not null } ? endpoint.Port : null,
                    certHolder?.Current?.NotAfter.ToUniversalTime(),
                    certHolder is not null ? certHolder.IsSelfSigned : null,
                    certHolder?.LastError, nodeHealthReports), stoppingToken);

                if (heartbeat.NextNonce is not null && heartbeat.NextNonce != _registration.CheckInNonce)
                {
                    _registration = _registration with { CheckInNonce = heartbeat.NextNonce };
                    TrySave();
                }

                logger.LogInformation("Config refreshed: {Cameras} camera route(s) across {Nodes} node(s).",
                    _routes.Count, config.Nodes.Count);

                // Auto-update: the server only ever hands this back when a genuinely newer approved
                // build exists for the proxy platform and NodeAutoUpdate.Enabled is on (see
                // LarisVMS.Web's proxy heartbeat handler) — nothing left to decide here except not
                // double-triggering while one is already in flight. A successful apply calls
                // IHostApplicationLifetime.StopApplication() itself, which unwinds this loop via the
                // stopping token. Mirrors NodeWorker.ReconcileLoopAsync.
                if (heartbeat.UpdateAvailable is not null && !updateService.IsApplying)
                {
                    logger.LogInformation("Proxy update available: {Version} — downloading and applying.",
                        heartbeat.UpdateAvailable.Version);
                    await updateService.TryApplyAsync(heartbeat.UpdateAvailable, stoppingToken);
                }
            }
            catch (Exception ex) when (IsRetryable(ex, stoppingToken))
            {
                logger.LogWarning(ex, "Config/heartbeat cycle failed — serving from the last known routes; will retry.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Failover plan phase 3: GET each assigned node's <c>/health</c> and turn the result
    /// into a quorum vote. A non-200, a timeout, or a body that doesn't parse is <em>down</em>; a
    /// clean 200 + parseable body is <em>up</em>. Runs once per config/heartbeat cycle (~30s) — the
    /// proxy is a secondary voter behind the partner node and central.</summary>
    private async Task<List<NodePartnerHealthReport>> ProbeAssignedNodesAsync(
        List<NodePartnerProbeDto>? nodes, CancellationToken ct)
    {
        var reports = new List<NodePartnerHealthReport>();
        if (nodes is null || nodes.Count == 0) return reports;

        foreach (var node in nodes)
        {
            if (ct.IsCancellationRequested) break;
            var now = DateTime.UtcNow;
            try
            {
                using var response = await _probeHttp.GetAsync(
                    $"http://{node.Host}:{node.Port}/health", HttpCompletionOption.ResponseContentRead, ct);
                if (!response.IsSuccessStatusCode)
                    reports.Add(new NodePartnerHealthReport(node.NodeId, false, now, $"HTTP {(int)response.StatusCode}"));
                else
                {
                    var body = await response.Content.ReadFromJsonAsync<NodeHealthDto>(ct);
                    reports.Add(new NodePartnerHealthReport(node.NodeId, body is not null, now,
                        body is null ? "unparseable /health body" : null));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                reports.Add(new NodePartnerHealthReport(node.NodeId, false, now,
                    ex is TaskCanceledException ? "timeout" : ex.GetType().Name));
            }
        }
        return reports;
    }

    private static Dictionary<Guid, NodeTarget> BuildRoutes(List<ProxyConfigNodeDto>? nodes)
    {
        var map = new Dictionary<Guid, NodeTarget>();
        if (nodes is null) return map;
        foreach (var n in nodes)
            foreach (var camId in n.CameraIds)
                map[camId] = new NodeTarget(n.NodeId, n.NodeHost, n.NodeLivePort);
        return map;
    }

    private void TrySave()
    {
        try { ProxyConfigStore.Save(_registration); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not persist proxy.config."); }
    }

    internal static bool IsRetryable(Exception ex, CancellationToken ct)
        => ex is not OperationCanceledException || !ct.IsCancellationRequested;
}
