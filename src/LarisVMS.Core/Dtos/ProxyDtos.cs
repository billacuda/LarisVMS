namespace LarisVMS.Core.Dtos;

// Failover plan phase 2: wire DTOs for the proxy control plane (POST /api/proxies/*). Shared via Core
// so LarisVMS.Proxy can (de)serialize them without referencing Infrastructure or Web. Auth mirrors
// the node control plane — bearer "{proxyId}:{secret}", plus the phase-5a rolling check-in nonce.

public record ProxyRegisterRequest(string RegistrationKey, string Hostname, string? Version, string? Platform);

public record ProxyRegisterResponse(Guid ProxyId, string Secret);

/// <summary>SentAtUtc is the proxy's own <c>DateTime.UtcNow</c> when it built the request (clock-skew
/// diagnostic, same as a node's). Nonce is the phase-5a rolling value echoed from the previous
/// response. ReportedPort / CertNotAfterUtc / CertIsSelfSigned / LastError describe what the proxy's
/// own HTTPS endpoint is currently doing. All defaulted so an older proxy build deserializes cleanly.</summary>
public record ProxyHeartbeatRequest(string? Version, DateTime? SentAtUtc = null, string? Nonce = null,
    int? ReportedPort = null, DateTime? CertNotAfterUtc = null, bool? CertIsSelfSigned = null, string? LastError = null,
    /// <summary>Failover plan phase 3: this proxy's <c>/health</c> reads of the recorder nodes it was
    /// told to probe (<see cref="ProxyConfigResponse.NodesToProbe"/>) — it is a third quorum voter
    /// alongside the partner node and central for any node it is assigned to. Null/empty from an older
    /// proxy build or one serving no nodes.</summary>
    List<NodePartnerHealthReport>? NodeHealthReports = null);

/// <summary>NextNonce (phase 5a): the single-use value the proxy must echo on its next heartbeat.
/// Null only on a 401 replay rejection.
///
/// UpdateAvailable is non-null only when a genuinely newer approved build exists for the proxy
/// platform (<c>proxy-win-x64</c>) and <c>NodeAutoUpdate.Enabled</c> is on — the exact same gate,
/// approval queue and comparison (<see cref="NodeVersionComparer"/>) the recorder-node auto-update
/// path uses, just keyed to the proxy platform. Defaulted so an older proxy build deserializes
/// cleanly and simply never self-updates.</summary>
public record ProxyHeartbeatResponse(int IntervalSeconds, string? NextNonce = null,
    ProxyUpdateInfoDto? UpdateAvailable = null);

/// <summary>Proxy auto-update: a genuinely newer <c>NodeBuildVersion</c> (Platform
/// <c>proxy-win-x64</c>) exists for this proxy. <see cref="DownloadUrl"/> is an absolute URL back to
/// this same server's <c>/api/proxies/download/{buildId}</c>, reusing the proxy's own Bearer
/// <c>{proxyId}:{secret}</c> credentials — same auth as every other <c>/api/proxies/*</c> route.
/// <see cref="Sha256"/> is what <c>LarisVMS.Proxy.ProxyUpdateService</c> verifies the download
/// against before applying it; a mismatch aborts without touching the running binary. There is no
/// Vision component here (that is recorder-node-only) — this is the plain shape of
/// <c>NodeUpdateInfoDto</c> minus those fields.</summary>
public record ProxyUpdateInfoDto(string Version, string DownloadUrl, string Sha256, long SizeBytes);

/// <summary>One recorder node this proxy may forward to — its plain-HTTP LAN address, plus every
/// camera currently recording on that node (so the proxy maps an incoming <c>/live/{cameraId}</c> to
/// a target without ever trusting a client-supplied address).</summary>
public record ProxyConfigNodeDto(Guid NodeId, string NodeHost, int NodeLivePort, List<Guid> CameraIds);

/// <summary>Everything the proxy needs to run: the camera→node address map, the certificate to serve
/// (path + password, password sent decrypted over this HTTPS channel like a node's client cert),
/// insecure-mode policy, and the public web origin for CORS. All defaulted for back-compat.</summary>
public record ProxyConfigResponse(List<ProxyConfigNodeDto> Nodes,
    string? CertPfxPath = null, string? CertPfxPassword = null, bool AllowInsecure = false, string? WebOrigin = null,
    /// <summary>Failover plan phase 3: the recorder nodes assigned to this proxy that it should probe
    /// for the recording-failover quorum — <c>http://{Host}:{Port}/health</c> each cycle, verdicts
    /// carried back in <see cref="ProxyHeartbeatRequest.NodeHealthReports"/>. Null/empty when the
    /// proxy has no assigned nodes.</summary>
    List<NodePartnerProbeDto>? NodesToProbe = null);
