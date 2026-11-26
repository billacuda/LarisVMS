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
    /// <summary>Per-viewer fragment queue depth. At the live pipe leg's ~500ms fragment cadence
    /// (RecordingSession.LiveFragDurationMicros) this is ~64s of slack before a viewer whose socket
    /// write can't keep up is considered genuinely broken. A client that falls that far behind is
    /// dropped with a clean close (below) rather than being fed a spliced byte stream.</summary>
    private const int LiveViewerQueueCapacity = 128;

    /// <summary>How long a viewer being dropped gets to take what's already queued for it. A send that
    /// never finishes (a client that stopped reading without closing) would otherwise hold its queue,
    /// tens of MB, for as long as the connection lasts.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(WebSocket socket, ILiveSource session, ILogger logger, CancellationToken ct)
    {
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // FullMode.Wait (not DropOldest, the old behaviour, nor DropWrite): when this viewer's send
        // loop can't keep up we need to *notice*, not silently discard a fragment from the middle of
        // the stream. Splicing a gap into a continuous fMP4 stream corrupts every fragment after it
        // for that browser's SourceBuffer (there is no mid-stream resync marker), which only surfaces
        // client-side as an opaque decode error. With Wait mode a synchronous TryWrite still returns
        // *false* immediately when the queue is full (only the async WriteAsync would block — and
        // OnFragment never calls it, so this can never back-pressure the source's stdout drain), so
        // the writer below can detect the overflow, complete the channel so the reader drains what is
        // already queued (all contiguous), and let the socket close cleanly — the client's proven
        // recovery path is a fresh reconnect with a new init segment. SingleWriter is now true:
        // LiveFragmentReceived is only ever raised from the source's one stdout-drain loop.
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(LiveViewerQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        // Both handlers run on the source's own loops, so neither may throw or block.
        void FinishSending()
        {
            channel.Writer.TryComplete();
            try { sendCts.CancelAfter(DrainTimeout); }
            catch (ObjectDisposedException) { /* this viewer has already left */ }
        }

        var fellBehind = 0;
        var attemptEnded = 0;
        void OnFragment(byte[] fragment)
        {
            if (Volatile.Read(ref attemptEnded) == 1) return; // not a continuation of what this viewer has
            if (!channel.Writer.TryWrite(fragment) && Interlocked.Exchange(ref fellBehind, 1) == 0)
            {
                logger.LogInformation(
                    "Live viewer fell behind (per-viewer queue of {Capacity} full) — closing socket for a clean reconnect.",
                    LiveViewerQueueCapacity);
                FinishSending();
            }
        }

        // The recorder's ffmpeg restarted. What comes next has another init segment and restarted
        // timestamps, which a browser's buffer can't take: end here, and the viewer reconnects to it.
        void OnAttemptEnded()
        {
            if (Interlocked.Exchange(ref attemptEnded, 1) == 0) FinishSending();
        }

        session.LiveAttemptEnded += OnAttemptEnded;
        try
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

            session.LiveFragmentReceived += OnFragment;
            await socket.SendAsync(initSegment, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token);

            await foreach (var fragment in channel.Reader.ReadAllAsync(sendCts.Token))
            {
                if (socket.State != WebSocketState.Open) break;
                await socket.SendAsync(fragment, WebSocketMessageType.Binary, endOfMessage: true, sendCts.Token);
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
            session.LiveAttemptEnded -= OnAttemptEnded;
            var (status, reason) = Volatile.Read(ref fellBehind) == 1
                ? (WebSocketCloseStatus.PolicyViolation, "viewer fell behind — reconnect")
                : (WebSocketCloseStatus.NormalClosure, (string?)null);
            await TryCloseAsync(socket, status, reason);
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string? description)
    {
        if (socket.State != WebSocketState.Open) return;
        // CloseAsync waits for the client to answer, which one that has gone away never does.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await socket.CloseAsync(status, description, timeout.Token); }
        catch { /* client already gone — nothing to clean up */ }
    }
}
