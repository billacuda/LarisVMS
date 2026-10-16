using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Services;

/// <summary>Who is talking through which camera right now. One operator per camera — a camera has one
/// speaker, and two people talking over each other through it is just noise. Single web server, so an
/// in-memory map is the whole story.</summary>
public class TalkSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, string> _talking = new();

    /// <summary>False when someone else already holds the camera; <paramref name="holder"/> is then their name.</summary>
    public bool TryAcquire(Guid cameraId, string who, out string holder)
    {
        holder = _talking.GetOrAdd(cameraId, who);
        return ReferenceEquals(holder, who);
    }

    public void Release(Guid cameraId, string who) => _talking.TryRemove(new KeyValuePair<Guid, string>(cameraId, who));
}

/// <summary>Relays two-way audio between the browser and the camera's node (see the node's
/// TalkSessionHandler for the wire protocol). Everything is passed through as-is except the node's
/// "recorded" report, which becomes a TalkRecording row and reaches the browser without the file path.
/// When the browser goes away mid-press, a "stop" is sent on its behalf and the node's report is
/// waited for briefly, so that press is still recorded.</summary>
public static class TalkRelay
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ReportWait = TimeSpan.FromSeconds(3);

    public sealed record Context(Guid CameraId, Guid NodeId, string? UserId, string? UserName, ITalkRecordingService Recordings, ILogger Logger);

    public static async Task RunAsync(WebSocket browser, WebSocket node, Context context, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fromNode = Task.Run(() => NodeToBrowserAsync(node, browser, context, stopping.Token), CancellationToken.None);
        try
        {
            await BrowserToNodeAsync(browser, node, stopping.Token);
            // The browser left (or released and closed). Make sure an open press is closed out and
            // recorded before the node socket goes: "stop" is harmless when nothing is open.
            if (node.State == WebSocketState.Open)
            {
                await SendTextAsync(node, """{"type":"stop"}""", CancellationToken.None);
                await Task.WhenAny(fromNode, Task.Delay(ReportWait, CancellationToken.None));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Either side dropping is the normal end of a relay.
        }
        finally
        {
            await stopping.CancelAsync();
            try { await fromNode; } catch { /* the relay is ending either way */ }
            await CloseAsync(node);
            await CloseAsync(browser);
        }
    }

    private static async Task BrowserToNodeAsync(WebSocket browser, WebSocket node, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (browser.State == WebSocketState.Open && node.State == WebSocketState.Open)
        {
            var result = await browser.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            await node.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }

    private static async Task NodeToBrowserAsync(WebSocket node, WebSocket browser, Context context, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (node.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await node.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    await message.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                } while (!result.EndOfMessage);

                var bytes = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Text && TryReadRecorded(bytes, out var recorded))
                {
                    await RecordAsync(recorded, context);
                    // Recording a press is the last thing the relay waits for when the browser has gone.
                    if (browser.State != WebSocketState.Open) return;
                    await SendTextAsync(browser, JsonSerializer.Serialize(new { type = "recorded", recorded.StartUtc, recorded.EndUtc }, Json), ct);
                    continue;
                }
                if (browser.State == WebSocketState.Open)
                    await browser.SendAsync(bytes, result.MessageType, true, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // The relay is ending; RunAsync closes both sockets.
        }
    }

    internal sealed record RecordedReport(DateTime StartUtc, DateTime EndUtc, string Path, long Bytes);

    internal static bool TryReadRecorded(byte[] json, out RecordedReport report)
    {
        report = null!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "recorded") return false;
            report = new RecordedReport(
                root.GetProperty("startUtc").GetDateTime().ToUniversalTime(),
                root.GetProperty("endUtc").GetDateTime().ToUniversalTime(),
                root.GetProperty("path").GetString() ?? "",
                root.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0);
            return report.Path.Length > 0;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static async Task RecordAsync(RecordedReport r, Context context)
    {
        try
        {
            await context.Recordings.AddAsync(new TalkRecording
            {
                CameraId = context.CameraId,
                NodeId = context.NodeId,
                UserId = context.UserId,
                UserName = context.UserName,
                StartUtc = r.StartUtc,
                EndUtc = r.EndUtc,
                FilePath = r.Path,
                SizeBytes = r.Bytes,
            });
        }
        catch (Exception ex)
        {
            // The audio still reached the camera and the file is on the node; only its timeline entry is lost.
            context.Logger.LogError(ex, "Couldn't save the two-way audio recording {Path}.", r.Path);
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private static async Task CloseAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Already closed or aborted by the other end.
        }
    }
}
