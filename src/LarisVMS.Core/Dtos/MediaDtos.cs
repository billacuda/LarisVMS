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

/// <summary>Client → Web live-view health beacon: one shared shape for the stutter/catch-up/decode
/// events live-view.js's drift controller can hit (see EventType), sent so they land in this app's
/// own logs instead of only a browser devtools console nobody was watching at the time. Best-effort,
/// logged only — not persisted.</summary>
/// <param name="Role">'main' or 'sub' — which of the camera's live-fMP4 sources this tile was on.</param>
/// <param name="StreamMode">'proxy' or 'direct' — the failover-plan routing this session used.</param>
/// <param name="EventType">"catchup" (sustained playbackRate speed-up), "hard_resync" (drift too
/// large, session torn down), "gap_jump" (currentTime fell out of the buffered range and was
/// resynced), "server_disconnect" (node closed the socket, viewer fell behind), "decode_health"
/// (periodic sample of decoder throughput — see live-view.js's driftTimer for how effectiveRate vs.
/// requestedRate distinguishes a decode-bound tile from a network/buffering one), "unexpected_pause"
/// (the element paused without this app asking it to — Detail carries the visibility/focus/buffer
/// state captured at that instant, since the cause has so far resisted being inferred after the
/// fact), "pause_resync" (playback resumed after such a pause and was resynced straight to near
/// the live edge, rather than waiting for driftTimer to discover the drift the slow way), or
/// "box_alignment" (periodic sample of how the AI-detection overlay is lining up with the video —
/// the video latency it measured, how old the detections themselves were, and the gap it is
/// interpolating across).</param>
/// <param name="Magnitude">Drift seconds for catchup/hard_resync/pause_resync, the WebSocket close
/// code for server_disconnect, effective-rate/requested-rate ratio for decode_health (below ~0.85
/// means the decoder itself is the bottleneck), measured video latency in ms for box_alignment,
/// null for gap_jump (no natural magnitude).</param>
/// <param name="Detail">Free-text context — current rate, close reason, jump reason, (for
/// decode_health) requested/effective rate and dropped-frame counts, or (for box_alignment) the
/// latency breakdown and whether it came from measurement or the fixed fallback.</param>
public record MediaStreamEventBeacon(Guid CameraId, string Role, string? StreamMode, string EventType, double? Magnitude, string? Detail);
