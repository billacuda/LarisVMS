using System.Net.WebSockets;

namespace LarisVMS.Relay;

/// <summary>
/// Failover plan phase 2: a dumb WebSocket byte relay for the proxy tier — browser ⇄ proxy ⇄ node.
/// The proxy parses nothing; it forwards frames with their <c>EndOfMessage</c> flags intact so a
/// message larger than the buffer reassembles correctly on the far side without ever being fully
/// buffered here. Mirrors LarisVMS.Web's own long-standing <c>ProxyLiveViewAsync</c>, just made
/// bidirectional so a browser's close/ping frames reach the node too.
/// </summary>
public static class WebSocketRelay
{
    private const int BufferSize = 64 * 1024;

    public static async Task RelayAsync(WebSocket a, WebSocket b, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumpAToB = PumpAsync(a, b, linked.Token);
        var pumpBToA = PumpAsync(b, a, linked.Token);

        await Task.WhenAny(pumpAToB, pumpBToA);
        await linked.CancelAsync();
        try { await Task.WhenAll(pumpAToB, pumpBToA); }
        catch { /* the loser was cancelled — expected */ }

        await CloseQuietly(a);
        await CloseQuietly(b);
    }

    private static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await source.ReceiveAsync(buffer, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close) break;

            try
            {
                await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task CloseQuietly(WebSocket ws)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* best effort */ }
        }
    }
}
