using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Web.Services;

/// <summary>How a browser should reach one camera's recorder node for live/playback.</summary>
public enum MediaStreamMode { Proxy, Direct }

/// <param name="Mode">Proxy = relay every byte through this tier (the original behaviour). Direct =
/// point the browser at <paramref name="DirectHost"/>:<paramref name="DirectPort"/> over HTTPS —
/// which since phase 2 may be a standalone <see cref="MediaProxy"/> rather than the node itself.</param>
/// <param name="Insecure">True when the endpoint is serving a self-signed fallback certificate.</param>
public record MediaRoute(MediaStreamMode Mode, string? DirectHost, int? DirectPort, bool Insecure)
{
    public static readonly MediaRoute Proxy = new(MediaStreamMode.Proxy, null, null, false);
}

/// <summary>The node fields the direct-to-node routing decision reads — a small record so the resolver
/// never needs a tracked <see cref="Node"/> and callers can project straight to it from EF.</summary>
public record MediaRouteInputs(string? DirectStreamingMode, bool? AllowInsecureClientEndpoint,
    string? ClientEndpointHost, int? ClientEndpointReportedPort, DateTime? ClientEndpointCertNotAfter,
    bool? ClientEndpointCertIsSelfSigned, string? ClientEndpointLastError)
{
    public static MediaRouteInputs From(Node n) => new(n.DirectStreamingMode, n.AllowInsecureClientEndpoint,
        n.ClientEndpointHost, n.ClientEndpointReportedPort, n.ClientEndpointCertNotAfter,
        n.ClientEndpointCertIsSelfSigned, n.ClientEndpointLastError);
}

/// <summary>
/// Failover plan phase 1/2: resolves, at ticket time, how a camera's live/playback bytes reach the
/// browser. Priority: a healthy assigned <see cref="MediaProxy"/> (primary, then backup) → direct to
/// the node's own client endpoint (when the toggle says Direct and it is healthy) → proxy through
/// LarisVMS.Web (the default). Fails safe at every step.
/// </summary>
public class MediaRoutingService(ISettingsResolver settings, ApplicationDbContext db)
{
    public async Task<MediaRoute> ResolveAsync(Node? node, CancellationToken ct = default)
    {
        if (node is null) return MediaRoute.Proxy;

        var proxyRoute = await ResolveProxyAsync(node, ct);
        if (proxyRoute is not null) return proxyRoute;

        return await ResolveDirectAsync(MediaRouteInputs.From(node), ct);
    }

    public async Task<MediaRoute> ResolveByNodeIdAsync(Guid? nodeId, CancellationToken ct = default)
    {
        if (nodeId is not { } id) return MediaRoute.Proxy;
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
        return await ResolveAsync(node, ct);
    }

    /// <summary>Failover plan phase 3: the node a browser should reach for this camera's <em>live</em>
    /// stream right now — its configured node normally, or the backup node while the configured one is
    /// <see cref="NodeFailoverState.FailedOverAway"/>. Playback needs none of this: it resolves per
    /// segment off <c>Segment.NodeId</c>, so footage recorded on the backup during an outage is
    /// already served from the backup. Returns <c>camera.Node</c> (the loaded primary) whenever no
    /// failover is in effect, so the common path costs one extra lightweight query and nothing else.</summary>
    public async Task<Node?> GetEffectiveRecordingNodeAsync(Camera? camera, CancellationToken ct = default)
    {
        if (camera?.NodeId is not { } primaryId) return camera?.Node;

        var states = await db.Nodes.AsNoTracking()
            .Select(n => new { n.Id, n.FailoverState, n.BackupNodeId }).ToListAsync(ct);
        var effectiveId = RecordingNodeResolver.Resolve(primaryId, camera.BackupNodeIdOverride,
            states.ToDictionary(x => x.Id, x => x.FailoverState),
            states.ToDictionary(x => x.Id, x => x.BackupNodeId));

        if (effectiveId is not { } id || id == primaryId) return camera.Node;
        return await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
    }

    // ── Phase 2: standalone proxy tier ──────────────────────────────────────────
    private async Task<MediaRoute?> ResolveProxyAsync(Node node, CancellationToken ct)
    {
        if (node.PrimaryProxyId is null && node.BackupProxyId is null) return null;

        var ids = new[] { node.PrimaryProxyId, node.BackupProxyId }
            .Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
        var proxies = await db.MediaProxies.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var nodeAllowInsecure = node.AllowInsecureClientEndpoint
            ?? await settings.GetAsync("LiveView.AllowInsecureClientEndpoint", false, ct: ct);

        foreach (var pid in new[] { node.PrimaryProxyId, node.BackupProxyId })
        {
            if (pid is not { } id || !proxies.TryGetValue(id, out var p)) continue;
            if (!p.Enabled || !p.Healthy || string.IsNullOrWhiteSpace(p.Host) || p.ReportedPort is not > 0) continue;
            if (p.CertNotAfter is { } na && na <= DateTime.UtcNow) continue;

            var selfSigned = p.CertIsSelfSigned == true;
            if (selfSigned && !(p.AllowInsecure ?? nodeAllowInsecure)) continue;

            return new MediaRoute(MediaStreamMode.Direct, p.Host, p.ReportedPort.Value, selfSigned);
        }
        return null;
    }

    // ── Phase 1: direct to the node's own client endpoint ───────────────────────
    public async Task<MediaRoute> ResolveDirectAsync(MediaRouteInputs? n, CancellationToken ct = default)
    {
        if (n is null) return MediaRoute.Proxy;

        var mode = !string.IsNullOrWhiteSpace(n.DirectStreamingMode)
            ? n.DirectStreamingMode!
            : await settings.GetAsync("LiveView.DirectStreaming", "Proxy", ct: ct);
        if (!string.Equals(mode, "Direct", StringComparison.OrdinalIgnoreCase))
            return MediaRoute.Proxy;

        var allowInsecure = n.AllowInsecureClientEndpoint
            ?? await settings.GetAsync("LiveView.AllowInsecureClientEndpoint", false, ct: ct);

        var certHealthy = n.ClientEndpointCertNotAfter is { } notAfter && notAfter > DateTime.UtcNow
                          && string.IsNullOrEmpty(n.ClientEndpointLastError);
        var selfSigned = n.ClientEndpointCertIsSelfSigned == true;

        if (string.IsNullOrWhiteSpace(n.ClientEndpointHost) || n.ClientEndpointReportedPort is not > 0 || !certHealthy)
            return MediaRoute.Proxy;

        if (selfSigned && !allowInsecure)
            return MediaRoute.Proxy;

        return new MediaRoute(MediaStreamMode.Direct, n.ClientEndpointHost, n.ClientEndpointReportedPort.Value, selfSigned);
    }

    // Kept for the phase-1 unit tests, which exercise the direct-to-node decision in isolation.
    public Task<MediaRoute> ResolveAsync(MediaRouteInputs? n, CancellationToken ct = default) => ResolveDirectAsync(n, ct);
}
