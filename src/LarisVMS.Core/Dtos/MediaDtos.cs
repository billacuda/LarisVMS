namespace LarisVMS.Core.Dtos;

/// <summary>Failover plan phase 1: the live-view ticket the browser resolves from
/// <c>GET /api/media/live-ticket/{cameraId}</c> before opening its video socket.
/// <list type="bullet">
/// <item><c>Mode == "proxy"</c> — connect to this Web host's own <c>/live/{id}</c> exactly as before;
/// <see cref="Token"/> is null (the /live route mints its own).</item>
/// <item><c>Mode == "direct"</c> — open <see cref="VideoUrl"/> (a <c>wss://</c> straight to the node)
/// with <c>?token=</c><see cref="Token"/>.</item>
/// </list>
/// <see cref="Insecure"/> and <see cref="TrustUrl"/> are set only when the node is serving its
/// self-signed fallback certificate, so the client can offer a one-click "trust this recorder" step.</summary>
public record MediaLiveTicket(string Mode, bool Insecure, string VideoUrl, string? TrustUrl, string? Token, int ExpiresInSeconds);

/// <summary>Failover plan phase 1: client → Web time-to-first-frame beacon, logged for the
/// direct-vs-proxy A/B. Not persisted.</summary>
public record MediaTimingBeacon(Guid CameraId, string Mode, double MsToFirstFrame);
