using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Infrastructure.Services;

/// <summary>
/// Failover plan phase 2: server side of the standalone relay tier. Auth mirrors NodeService — bearer
/// "{proxyId}:{secret}", SHA-256 hashed with fixed-time compare, PreviousApiKeyHash as a grace slot,
/// plus the phase-5a rolling check-in nonce. A proxy holds no per-node secret and no camera
/// credentials, so there is no MediaSigningKey to hand out and (this pass) no secret rotation.
/// </summary>
public class ProxyService(ApplicationDbContext db, ISettingsResolver settings, ILogger<ProxyService>? logger = null) : IProxyService
{
    public async Task<List<MediaProxy>> ListAsync(CancellationToken ct = default)
        => await db.MediaProxies.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<ProxyRegisterResponse> RegisterAsync(ProxyRegisterRequest request, CancellationToken ct = default)
    {
        // Reuses the node registration key — one shared secret for adding recorders or proxies.
        var expectedKey = await settings.GetRawAsync("Node.RegistrationKey", ct: ct);
        if (string.IsNullOrEmpty(expectedKey) || !SecretHash.FixedTimeEquals(request.RegistrationKey ?? "", expectedKey))
            throw new UnauthorizedAccessException("Invalid registration key.");

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var proxy = new MediaProxy
        {
            Id = Guid.NewGuid(),
            Name = request.Hostname,
            // A sensible default an admin corrects on Admin/Proxies to the actual routable FQDN.
            Host = request.Hostname,
            ApiKeyHash = SecretHash.Hash(secret),
            Version = request.Version,
            Enabled = true,
            LastSeenAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        };
        db.MediaProxies.Add(proxy);
        await db.SaveChangesAsync(ct);
        return new ProxyRegisterResponse(proxy.Id, secret);
    }

    public async Task<MediaProxy?> AuthenticateAsync(string proxyId, string secret, string? remoteIp, CancellationToken ct = default)
    {
        if (!Guid.TryParse(proxyId, out var id)) return null;
        var proxy = await db.MediaProxies.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (proxy is null) return null;

        var matchesCurrent = SecretHash.Matches(secret, proxy.ApiKeyHash);
        var matchesPrevious = proxy.PreviousApiKeyHash is not null && SecretHash.Matches(secret, proxy.PreviousApiKeyHash);
        if (!matchesCurrent && !matchesPrevious) return null;

        proxy.LastSeenAt = DateTime.UtcNow;
        if (remoteIp is not null) proxy.LastIpAddress = remoteIp;
        await db.SaveChangesAsync(ct);
        return proxy;
    }

    public async Task<NodeCheckInSecurity> ApplyCheckInSecurityAsync(Guid proxyId, string? presentedNonce, CancellationToken ct = default)
    {
        var proxy = await db.MediaProxies.FirstOrDefaultAsync(p => p.Id == proxyId, ct);
        if (proxy is null) return new NodeCheckInSecurity(false, null, null);

        // Same absent-vs-wrong handling as the node nonce gate — never `presentedNonce ?? ""`.
        if (proxy.CheckInNonce is not null)
        {
            if (presentedNonce is null)
                proxy.CheckInNonce = null;
            else if (!SecretHash.FixedTimeEquals(presentedNonce, proxy.CheckInNonce))
                return new NodeCheckInSecurity(ReplayRejected: true, null, null);
        }

        var next = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        proxy.CheckInNonce = next;
        await db.SaveChangesAsync(ct);
        return new NodeCheckInSecurity(false, next, null);
    }

    public async Task RecordHeartbeatAsync(Guid proxyId, string? version, DateTime? sentAtUtc, DateTime serverReceivedUtc,
        int? reportedPort, DateTime? certNotAfter, bool? certIsSelfSigned, string? lastError,
        List<NodePartnerHealthReport>? nodeHealthReports = null, CancellationToken ct = default)
    {
        // Failover plan phase 3: this proxy's outgoing quorum votes on the nodes it serves. Same
        // "current build always sends a list, older one sends null → preserve" shape as the node side.
        var reportsJson = nodeHealthReports is not null
            ? System.Text.Json.JsonSerializer.Serialize(nodeHealthReports) : null;

        await db.MediaProxies.Where(p => p.Id == proxyId).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Version, p => version ?? p.Version)
            .SetProperty(p => p.ReportedPort, reportedPort)
            .SetProperty(p => p.CertNotAfter, certNotAfter)
            .SetProperty(p => p.CertIsSelfSigned, certIsSelfSigned)
            // The endpoint-standup error the proxy self-reports; ProxyHealthMonitor separately owns
            // the reachability verdict via UpdateHealthAsync.
            .SetProperty(p => p.LastError, lastError)
            .SetProperty(p => p.NodeHealthReportsJson, p => reportsJson ?? p.NodeHealthReportsJson), ct);
    }

    public async Task UpdateHealthAsync(Guid id, bool healthy, string? error, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await db.MediaProxies.Where(p => p.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Healthy, healthy)
            .SetProperty(p => p.LastHealthyAt, p => healthy ? now : p.LastHealthyAt)
            .SetProperty(p => p.LastError, p => healthy ? null : (error ?? p.LastError)), ct);
    }

    public async Task<ProxyConfigResponse> GetConfigAsync(Guid proxyId, CancellationToken ct = default)
    {
        var proxy = await db.MediaProxies.AsNoTracking()
            .Where(p => p.Id == proxyId)
            .Select(p => new { p.CertPfxPath, p.CertPfxPassword, p.AllowInsecure })
            .FirstOrDefaultAsync(ct);

        var assignedNodes = await db.Nodes.AsNoTracking()
            .Where(n => n.PrimaryProxyId == proxyId || n.BackupProxyId == proxyId)
            .Where(n => n.LastIpAddress != null && n.LivePort != null)
            .Select(n => new { n.Id, n.LastIpAddress, n.LivePort })
            .ToListAsync(ct);

        var nodeIds = assignedNodes.Select(n => n.Id).ToList();
        var camerasByNode = (await db.Cameras.AsNoTracking()
                .Where(c => c.NodeId != null && nodeIds.Contains(c.NodeId!.Value) && c.IsEnabled)
                .Select(c => new { c.Id, NodeId = c.NodeId!.Value })
                .ToListAsync(ct))
            .GroupBy(c => c.NodeId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        var nodeDtos = assignedNodes.Select(n => new ProxyConfigNodeDto(
            n.Id, n.LastIpAddress!, n.LivePort!.Value,
            camerasByNode.TryGetValue(n.Id, out var cams) ? cams : [])).ToList();

        // Failover plan phase 3: the same assigned nodes, as quorum-probe targets — this proxy is a
        // third voter (behind the partner node and central) for any node it serves.
        var nodesToProbe = assignedNodes
            .Select(n => new NodePartnerProbeDto(n.Id, n.LastIpAddress!, n.LivePort!.Value))
            .ToList();

        var allowInsecure = proxy?.AllowInsecure
            ?? await settings.GetAsync("LiveView.AllowInsecureClientEndpoint", false, ct: ct);
        var publicOrigin = await settings.GetAsync<string?>("LiveView.PublicOrigin", null, ct: ct);

        return new ProxyConfigResponse(nodeDtos,
            string.IsNullOrWhiteSpace(proxy?.CertPfxPath) ? null : proxy!.CertPfxPath,
            string.IsNullOrWhiteSpace(proxy?.CertPfxPassword) ? null : proxy!.CertPfxPassword,
            allowInsecure,
            string.IsNullOrWhiteSpace(publicOrigin) ? null : publicOrigin,
            nodesToProbe.Count == 0 ? null : nodesToProbe);
    }

    public async Task UpdateAsync(Guid id, string name, string host, int port, bool enabled, string? certPfxPath,
        string? certPfxPassword, bool? allowInsecure, CancellationToken ct = default)
    {
        var proxy = await db.MediaProxies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new InvalidOperationException("Proxy not found.");
        proxy.Name = name;
        proxy.Host = host.Trim();
        proxy.Port = port;
        proxy.Enabled = enabled;
        proxy.CertPfxPath = string.IsNullOrWhiteSpace(certPfxPath) ? null : certPfxPath.Trim();
        proxy.AllowInsecure = allowInsecure;
        if (certPfxPassword is not null)
            proxy.CertPfxPassword = certPfxPassword.Length == 0 ? null : certPfxPassword;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        // NoAction FKs — null the assignments first so the delete doesn't violate them.
        await db.Nodes.Where(n => n.PrimaryProxyId == id).ExecuteUpdateAsync(u => u.SetProperty(n => n.PrimaryProxyId, (Guid?)null), ct);
        await db.Nodes.Where(n => n.BackupProxyId == id).ExecuteUpdateAsync(u => u.SetProperty(n => n.BackupProxyId, (Guid?)null), ct);
        await db.MediaProxies.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
    }
}
