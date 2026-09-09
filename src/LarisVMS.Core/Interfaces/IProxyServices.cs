using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>Failover plan phase 2: server side of the proxy control plane (registration, auth, config
/// snapshot) and the admin CRUD for the standalone relay tier.</summary>
public interface IProxyService
{
    Task<List<MediaProxy>> ListAsync(CancellationToken ct = default);

    Task<ProxyRegisterResponse> RegisterAsync(ProxyRegisterRequest request, CancellationToken ct = default);

    /// <summary>Validates "{proxyId}:{secret}" and stamps LastSeenAt/LastIpAddress. Null = unknown or
    /// invalid (401). Same shape as INodeService.AuthenticateAsync.</summary>
    Task<MediaProxy?> AuthenticateAsync(string proxyId, string secret, string? remoteIp, CancellationToken ct = default);

    /// <summary>The camera→node address map plus cert/insecure/origin config the proxy needs to run.</summary>
    Task<ProxyConfigResponse> GetConfigAsync(Guid proxyId, CancellationToken ct = default);

    /// <summary>Phase 5a rolling-nonce replay check on the heartbeat (no secret rotation for proxies
    /// in this pass — <see cref="NodeCheckInSecurity.NewSecret"/> is always null).</summary>
    Task<NodeCheckInSecurity> ApplyCheckInSecurityAsync(Guid proxyId, string? presentedNonce, CancellationToken ct = default);

    Task RecordHeartbeatAsync(Guid proxyId, string? version, DateTime? sentAtUtc, DateTime serverReceivedUtc,
        int? reportedPort, DateTime? certNotAfter, bool? certIsSelfSigned, string? lastError,
        List<NodePartnerHealthReport>? nodeHealthReports = null, CancellationToken ct = default);

    /// <summary>ProxyHealthMonitor's per-probe write of the /health result.</summary>
    Task UpdateHealthAsync(Guid id, bool healthy, string? error, CancellationToken ct = default);

    /// <summary>Edit an existing (self-registered) proxy — its routable host, expected port, cert, and
    /// enabled flag. <paramref name="certPfxPassword"/> is write-only: null leaves the stored value
    /// untouched, empty clears it.</summary>
    Task UpdateAsync(Guid id, string name, string host, int port, bool enabled, string? certPfxPath,
        string? certPfxPassword, bool? allowInsecure, CancellationToken ct = default);

    /// <summary>Removes a proxy, first nulling any Node.PrimaryProxyId/BackupProxyId that point at it.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
