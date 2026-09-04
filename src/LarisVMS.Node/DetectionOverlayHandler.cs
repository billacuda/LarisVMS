using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using LarisVMS.Core.Dtos;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Node;

/// <summary>
/// Streams one camera's live AI-detection snapshot to a connected WebSocket viewer — object
/// detection plan decision 6's live-view box overlay.
///
/// Unlike LiveViewerHandler (event-driven: pushed the instant a new fMP4 fragment arrives), this
/// polls LarisVMS.Vision.Service's own GET /cameras/{id}/detections at a fixed tick rate, since
/// Vision Service's control API is deliberately stateless/request-response (see
/// VisionLiveDetectionsResponse's own doc comment for why) — no persistent connection between Node
/// and Vision Service to manage for this. A separate, parallel WS from the video stream itself
/// (decision 6's own reasoning: keeping this off the already-delicate binary fMP4 relay).
///
/// Deliberately carries no color — Vision Service has no database access at all, the same way Node
/// itself never touches SQL Server directly (see VisionLiveDetectionsResponse's own doc comment).
/// LarisVMS.Web's own live-view proxy, one layer further out, is what attaches each category's
/// color before relaying this on to a browser.
/// </summary>
public static class DetectionOverlayHandler
{
    // ~6.7Hz — within decision 6's stated 5-10Hz range; box overlays don't need per-video-frame
    // precision the way the actual video stream does.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    public static async Task RunAsync(WebSocket socket, Guid cameraId, HttpClient visionHttp, ILogger logger, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var boxes = await FetchBoxesAsync(cameraId, visionHttp, logger, ct);
                var json = JsonSerializer.SerializeToUtf8Bytes(boxes);
                await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);

                try { await Task.Delay(PollInterval, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "Detection overlay viewer for camera {CameraId} disconnected.", cameraId);
        }
        finally
        {
            await TryCloseAsync(socket);
        }
    }

    /// <summary>Empty (never null) whenever Vision Service isn't currently watching this camera, or
    /// is unreachable — the browser side just shows no boxes for that tick, not an error; a single
    /// failed poll is routine (Vision Service restarting, a camera mid-reconnect) and must not tear
    /// down the whole viewer connection over it.</summary>
    private static async Task<List<VisionLiveDetectionBox>> FetchBoxesAsync(Guid cameraId, HttpClient visionHttp, ILogger logger, CancellationToken ct)
    {
        // Bounded well below the shared client's own timeout: this runs at PollInterval per viewer,
        // so a Vision Service that has gone slow should cost this viewer a frame of boxes, not let
        // polls queue up behind each other for seconds. A skipped tick is already a supported
        // outcome here — see this method's doc comment.
        using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollTimeout.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            var response = await visionHttp.GetAsync($"/cameras/{cameraId}/detections", pollTimeout.Token);
            if (!response.IsSuccessStatusCode) return [];

            var snapshot = await response.Content.ReadFromJsonAsync<VisionLiveDetectionsResponse>(pollTimeout.Token);
            return snapshot?.Boxes ?? [];
        }
        // A poll that outran its own 2s budget while the viewer is still connected is a skipped tick,
        // not a disconnect — only the caller's own ct ending the loop should propagate.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug("Detection poll for camera {CameraId} timed out — showing no boxes this tick.", cameraId);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to poll Vision Service for camera {CameraId}'s live detections — will retry.", cameraId);
            return [];
        }
    }

    private static async Task TryCloseAsync(WebSocket socket)
    {
        if (socket.State != WebSocketState.Open) return;
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
        catch { /* client already gone — nothing to clean up */ }
    }
}
