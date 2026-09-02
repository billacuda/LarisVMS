using System.Net.WebSockets;
using System.Text.Json;
using LarisVMS.Core.Dtos;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Node;

/// <summary>
/// Streams one camera's live per-zone motion scores to a connected WebSocket viewer — object
/// detection plan pass 3c-1's live per-zone wash on the Zones editor.
///
/// Poll-driven, same shape as DetectionOverlayHandler, not channel-buffered like LiveViewerHandler —
/// and for the same underlying reason even though this is in-process (unlike Vision Service's
/// detections, no HTTP hop needed): MotionSession.GetCurrentZoneScores' backing state is a plain
/// volatile reference swap written from ReadFramesAsync's own frame-reading loop (see that method's
/// own doc comment), and a slow/stalled viewer connection must never be able to stall motion scoring
/// itself by being invoked directly from it. Reading the latest snapshot on this handler's own
/// schedule keeps the two fully decoupled.
/// </summary>
public static class MotionZoneOverlayHandler
{
    // Matches MotionSessionOptions' own default Fps (5) — no benefit polling faster than the score
    // itself updates.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    // The Zones editor already has each zone's Sensitivity/Kind/enabled state loaded (it's editing
    // them) — see MotionZoneScoreDto's own doc comment for why the payload carries nothing more than
    // this, unlike DetectionOverlayHandler's boxes (which the Web tier does augment with a DB color).
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task RunAsync(WebSocket socket, Guid cameraId, NodeWorker worker, ILogger logger, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var session = worker.TryGetMotionSession(cameraId);
                var scores = session?.GetCurrentZoneScores();
                List<MotionZoneScoreDto> zonePayload = scores is null
                    ? []
                    : scores.Select(kv => new MotionZoneScoreDto(kv.Key, kv.Value)).ToList();
                var payload = new MotionZoneOverlayPayload(zonePayload, session?.GetCurrentCellScores()?.ToList());

                var json = JsonSerializer.SerializeToUtf8Bytes(payload, CamelCaseJson);
                await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);

                try { await Task.Delay(PollInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "Motion zone overlay viewer for camera {CameraId} disconnected.", cameraId);
        }
        finally
        {
            await TryCloseAsync(socket);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket)
    {
        if (socket.State != WebSocketState.Open) return;
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
        catch { /* client already gone — nothing to clean up */ }
    }
}
