using System.Net.WebSockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using LarisVMS.Media;

namespace LarisVMS.Node;

/// <summary>
/// Streams one camera's live fMP4 fanout to one connected WebSocket viewer: waits for (or reuses)
/// the current ffmpeg attempt's init segment, sends it once, then forwards every subsequent
/// fragment. Each viewer gets its own bounded channel between the source's stdout-drain loop and
/// its WebSocket send loop specifically so a slow client (or a stalled network write) can never
/// back-pressure that drain loop — which, for the Main source (RecordingSession), is also what's
/// keeping ffmpeg's stdout pipe (and therefore the whole tee, recording leg included) from blocking.
///
/// Takes <see cref="ILiveSource"/> rather than RecordingSession directly (M18: adaptive streaming)
/// so the same handler serves a viewer from either RecordingSession's live tee leg (Main) or
/// SubLiveSession (Sub) without needing to know which — the caller picks the source, this just
/// drains it.
/// </summary>
public static class LiveViewerHandler
{
    public static async Task RunAsync(WebSocket socket, ILiveSource session, ILogger logger, CancellationToken ct)
    {
        byte[] initSegment;
        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(15));
            initSegment = await session.WaitForLiveInitSegmentAsync(waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await TryCloseAsync(socket, WebSocketCloseStatus.InternalServerError, "no live stream available yet");
            return;
        }

        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        void OnFragment(byte[] fragment) => channel.Writer.TryWrite(fragment);
        session.LiveFragmentReceived += OnFragment;

        try
        {
            await socket.SendAsync(initSegment, WebSocketMessageType.Binary, endOfMessage: true, ct);

            await foreach (var fragment in channel.Reader.ReadAllAsync(ct))
            {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(fragment, WebSocketMessageType.Binary, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "Live viewer disconnected.");
        }
        finally
        {
            session.LiveFragmentReceived -= OnFragment;
            await TryCloseAsync(socket, WebSocketCloseStatus.NormalClosure, null);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        if (socket.State != WebSocketState.Open) return;
        try { await socket.CloseAsync(status, description, CancellationToken.None); }
        catch { /* client already gone — nothing to clean up */ }
    }
}
