using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LarisVMS.Core.Dtos;
using LarisVMS.Media.Talk;

namespace LarisVMS.Node;

/// <summary>Two-way audio, node side: one operator talking through one camera's speaker. LarisVMS.Web
/// relays the browser's WebSocket here (it never connects to a node directly for talk).
///
/// Wire protocol, both directions on the one socket:
///  - in, text: <c>{"type":"start"}</c> when push-to-talk is pressed, <c>{"type":"stop"}</c> when released
///  - in, binary: 16-bit little-endian PCM, 8 kHz mono, only between start and stop
///  - out, text: <c>{"type":"ready","codec":"PCMU"}</c> once the camera's backchannel is open;
///    <c>{"type":"error","message":"…"}</c> if it can't be; and after each stop
///    <c>{"type":"recorded","startUtc":…,"endUtc":…,"path":…,"bytes":…}</c> describing the WAV the
///    press was saved to (Web turns that into a TalkRecording row and doesn't pass it on).
///
/// Every press is recorded — what the operator said, alongside the camera's own microphone that's
/// already in the video recording — as <c>cam-{id}/talk/yyyy/MM/dd/{start}.wav</c> under the storage
/// root. StorageManager ages those out with the camera's retention.</summary>
public static class TalkSessionHandler
{
    public const int SampleRate = 8000;
    /// <summary>A stuck button (or a lost "stop") shouldn't open the camera's speaker indefinitely.</summary>
    public static readonly TimeSpan MaxPress = TimeSpan.FromMinutes(5);
    /// <summary>Presses shorter than this are accidental clicks — not worth a recording.</summary>
    public static readonly TimeSpan MinRecorded = TimeSpan.FromMilliseconds(300);

    private static readonly ConcurrentDictionary<Guid, byte> Busy = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string TalkDirectory(string storageRoot, Guid cameraId) => Path.Combine(storageRoot, $"cam-{cameraId}", "talk");

    /// <param name="onRecorded">Called with each saved press's file and start time — the footage
    /// store queues it for encryption at rest when the camera encrypts.</param>
    public static async Task RunAsync(WebSocket socket, Guid cameraId, NodeConfigCameraDto camera, string storageRoot,
        ILogger logger, CancellationToken ct, Action<string, DateTime>? onRecorded = null)
    {
        if (!Busy.TryAdd(cameraId, 0))
        {
            await SendAsync(socket, new { type = "error", message = "Someone else is already talking through this camera." }, ct);
            await CloseAsync(socket);
            return;
        }

        ITalkSink? backchannel = null;
        WavWriter? recording = null;
        DateTime pressStartUtc = default;
        try
        {
            var stream = camera.Streams.FirstOrDefault(s => s.Role == "Main") ?? camera.Streams.FirstOrDefault();
            if (stream is null || !Uri.TryCreate(stream.RtspUri, UriKind.Absolute, out var rtspUri))
            {
                await SendAsync(socket, new { type = "error", message = "This camera has no stream to open two-way audio on." }, ct);
                return;
            }

            try
            {
                backchannel = await OpenSinkAsync(camera, rtspUri, logger, ct);
            }
            catch (BackchannelException ex)
            {
                logger.LogWarning(ex, "Two-way audio to {Camera} failed to open.", camera.Name);
                await SendAsync(socket, new { type = "error", message = ex.Message }, ct);
                return;
            }

            await SendAsync(socket, new { type = "ready", codec = backchannel.Codec }, ct);

            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                message.SetLength(0);
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                // A stuck button (or a lost "stop") ends the press as if released. Checked per message
                // rather than with a receive timeout — cancelling a WebSocket receive aborts the socket.
                if (recording is not null && DateTime.UtcNow - pressStartUtc > MaxPress)
                {
                    logger.LogInformation("Two-way audio press on {Camera} hit the {Minutes}-minute limit.", camera.Name, MaxPress.TotalMinutes);
                    await FinishPressAsync(socket, recording, pressStartUtc, logger, ct, onRecorded);
                    recording = null;
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var type = ReadType(message.ToArray());
                    if (type == "start" && recording is null)
                    {
                        pressStartUtc = DateTime.UtcNow;
                        recording = new WavWriter(RecordingPath(storageRoot, cameraId, pressStartUtc), SampleRate);
                    }
                    else if (type == "stop" && recording is not null)
                    {
                        await backchannel.EndTalkspurtAsync(ct);
                        await FinishPressAsync(socket, recording, pressStartUtc, logger, ct, onRecorded);
                        recording = null;
                    }
                    continue;
                }

                if (recording is null || message.Length == 0) continue; // audio outside a press is ignored
                var pcm = message.GetBuffer().AsMemory(0, (int)message.Length);
                recording.Write(pcm.Span);
                await backchannel.SendPcmAsync(pcm, ct);
            }
        }
        catch (BackchannelException ex)
        {
            logger.LogWarning(ex, "Two-way audio to {Camera} dropped.", camera.Name);
            await TrySendAsync(socket, new { type = "error", message = ex.Message });
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        finally
        {
            if (recording is not null) await FinishPressAsync(socket, recording, pressStartUtc, logger, CancellationToken.None, onRecorded);
            if (backchannel is not null) await backchannel.DisposeAsync();
            Busy.TryRemove(cameraId, out _);
            await CloseAsync(socket);
        }
    }

    /// <summary>The standard ONVIF audio backchannel first; for a Dahua-family camera (its event
    /// integration is in use) whose firmware refuses that, Dahua's own HTTP audio API. The ONVIF
    /// error is what the operator sees if both fail — it's the one that applies to every camera.</summary>
    private static async Task<ITalkSink> OpenSinkAsync(NodeConfigCameraDto camera, Uri rtspUri, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await OnvifBackchannelClient.ConnectAsync(rtspUri, camera.Username, camera.Password, logger, ct);
        }
        catch (BackchannelException onvifError) when (camera.IntegrationKey == LarisVMS.Core.DahuaCgiIntegrationProvider.ProviderKey
                                                     && Uri.TryCreate(camera.IntegrationBaseUri, UriKind.Absolute, out var dahuaBase))
        {
            logger.LogInformation("ONVIF audio backchannel refused by {Camera} ({Reason}) — trying the Dahua audio API.", camera.Name, onvifError.Message);
            try
            {
                return await DahuaAudioPostClient.ConnectAsync(dahuaBase, camera.Username, camera.Password, logger, ct);
            }
            catch (BackchannelException dahuaError)
            {
                logger.LogWarning(dahuaError, "Dahua audio API also failed for {Camera}.", camera.Name);
                throw onvifError;
            }
        }
    }

    internal static string RecordingPath(string storageRoot, Guid cameraId, DateTime startUtc) =>
        Path.Combine(TalkDirectory(storageRoot, cameraId), startUtc.ToString("yyyy"), startUtc.ToString("MM"), startUtc.ToString("dd"),
            startUtc.ToString("yyyyMMdd'T'HHmmss'.'fff'Z'") + ".wav");

    /// <summary>Closes the press's WAV and reports it — or deletes it when it's an accidental click.</summary>
    private static async Task FinishPressAsync(WebSocket socket, WavWriter recording, DateTime startUtc, ILogger logger, CancellationToken ct,
        Action<string, DateTime>? onRecorded = null)
    {
        var duration = recording.Duration;
        var path = recording.Path;
        var bytes = recording.DataBytes + 44;
        recording.Dispose();

        if (duration < MinRecorded)
        {
            try { File.Delete(path); } catch (IOException) { }
            return;
        }

        // End = start + the audio actually received, not the wall clock at release: what's in the
        // file is what lines up against the video.
        var endUtc = startUtc + duration;
        logger.LogInformation("Recorded {Seconds:F1}s of two-way audio to {File}.", duration.TotalSeconds, Path.GetFileName(path));
        await TrySendAsync(socket, new { type = "recorded", startUtc, endUtc, path, bytes });
        onRecorded?.Invoke(path, startUtc);
    }

    internal static string? ReadType(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static Task SendAsync(WebSocket socket, object payload, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload, Json), WebSocketMessageType.Text, true, ct);

    private static async Task TrySendAsync(WebSocket socket, object payload)
    {
        if (socket.State != WebSocketState.Open) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendAsync(socket, payload, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
    }
}
