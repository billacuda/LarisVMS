namespace LarisVMS.Core.Entities;

/// <summary>
/// Failover plan phase 2: a standalone relay service (LarisVMS.Proxy) that sits between browsers and
/// recorder nodes for live/playback — a dumb TLS-terminating pass-through. It holds no per-node
/// secret and no camera credentials; only its own certificate and, at runtime, the list of node
/// addresses it may forward to (fetched from <c>/api/proxies/config</c>). Auth is the same bearer
/// <c>{proxyId}:{secret}</c> shape as a node, with the same rolling check-in nonce (phase 5).
///
/// A node can be assigned a primary + backup proxy (<see cref="Node.PrimaryProxyId"/> /
/// <see cref="Node.BackupProxyId"/>). Web health-checks every proxy and, at ticket time, resolves the
/// client to the first healthy one — primary, then backup, then direct-to-node, then central.
/// </summary>
public class MediaProxy
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Routable FQDN a browser dials — must match the proxy's certificate CN/SAN.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The proxy's own HTTPS listen port (what an admin expects it to serve on). The proxy
    /// also self-reports the port it actually bound in <see cref="ReportedPort"/>.</summary>
    public int Port { get; set; }

    public string ApiKeyHash { get; set; } = string.Empty;
    public string? PreviousApiKeyHash { get; set; }

    /// <summary>Phase 5a rolling check-in nonce — same semantics as <see cref="Node.CheckInNonce"/>.</summary>
    public string? CheckInNonce { get; set; }

    /// <summary>Path to the <c>.pfx</c> the proxy should serve, and its password (encrypted at rest,
    /// pushed decrypted over the HTTPS control channel — same handling as a node's client cert). Null
    /// = the proxy falls back to a local <c>proxy-endpoint.json</c> or its self-signed cert.</summary>
    public string? CertPfxPath { get; set; }
    public string? CertPfxPassword { get; set; }

    /// <summary>Per-proxy override of the global <c>LiveView.AllowInsecureClientEndpoint</c> flag.</summary>
    public bool? AllowInsecure { get; set; }

    public bool Enabled { get; set; } = true;
    public string? Version { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastIpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Health, as of the last ProxyHealthMonitor probe (Web polls /health every ~15s) ─────────────
    public bool Healthy { get; set; }
    public DateTime? LastHealthyAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? CertNotAfter { get; set; }
    public bool? CertIsSelfSigned { get; set; }
    public int? ReportedPort { get; set; }

    /// <summary>Failover plan phase 3: this proxy's outgoing quorum votes — same JSON shape and
    /// purpose as <see cref="Node.PartnerHealthReportsJson"/>, keyed by subject node id, from the
    /// proxy's own <c>/health</c> probes of the nodes assigned to it. A proxy is a third voter for
    /// any node it serves.</summary>
    public string? NodeHealthReportsJson { get; set; }

    public ICollection<Node> PrimaryForNodes { get; set; } = [];
    public ICollection<Node> BackupForNodes { get; set; } = [];
}
