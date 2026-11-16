using System.Net.WebSockets;

namespace LarisVMS.Web.Services;

/// <summary>
/// Copies a node's live WebSocket (the same fMP4 fanout every browser viewer reads — one init segment,
/// then a fragment per message) to a plain HTTP response body, so programs that only read a URL
/// (Home Assistant's Generic Camera, ffmpeg, VLC) can play it without the camera being asked for a
/// second stream.
///
/// A WebSocket viewer can take a new init segment mid-stream when the node restarts its ffmpeg; a
/// progressive MP4 reader can't, so the response ends there instead and the program reconnects.
/// </summary>
public static class LiveHttpRelay
{
    /// <summary>Whether a message is an init segment: an MP4 that starts with an "ftyp" box.</summary>
    internal static bool IsInitSegment(ReadOnlySpan<byte> message) =>
        message.Length >= 8 && message[4] == (byte)'f' && message[5] == (byte)'t' && message[6] == (byte)'y' && message[7] == (byte)'p';

    /// <summary>Relays until the node, the reader or <paramref name="ct"/> ends it. Returns how many
    /// messages were written.</summary>
    public static async Task<int> CopyAsync(WebSocket node, Stream output, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        var written = 0;
        try
        {
            while (node.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await node.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return written;
                    await message.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Binary || message.Length == 0) continue;
                var bytes = message.GetBuffer().AsMemory(0, (int)message.Length);
                // Nothing before the first init segment is playable on its own.
                if (written == 0 && !IsInitSegment(bytes.Span)) continue;
                if (written > 0 && IsInitSegment(bytes.Span)) return written;

                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
                written++;
            }
        }
        catch (OperationCanceledException) { /* the reader went away, or access ended */ }
        catch (WebSocketException) { /* the node dropped the connection */ }
        catch (IOException) { /* the reader dropped the connection */ }
        finally
        {
            if (node.State == WebSocketState.Open)
            {
                try { await node.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException) { /* already gone */ }
            }
        }
        return written;
    }
}
