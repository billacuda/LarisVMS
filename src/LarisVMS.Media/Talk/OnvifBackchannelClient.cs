using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Media.Talk;

/// <summary>Raised when a camera can't take two-way audio: it refused the backchannel, didn't offer
/// one, or the RTSP exchange failed. The message is shown to the operator.</summary>
public sealed class BackchannelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Sends audio to a camera's speaker over the ONVIF RTSP audio backchannel (ONVIF Streaming
/// spec, "Back channel connection"). ffmpeg can't do this, so it's a small RTSP client of its own:
/// DESCRIBE/SETUP/PLAY with <c>Require: www.onvif.org/ver20/backchannel</c>, then G.711 RTP packets
/// interleaved on the RTSP TCP connection (RFC 2326 §10.12). OPTIONS keeps the session alive; anything
/// the camera sends back (RTCP, keepalive replies) is read and discarded.
///
/// Audio in is little-endian 16-bit PCM at 8 kHz mono — what the browser sends — and goes out as
/// 20 ms packets. When audio stops (push-to-talk released) and later resumes, the RTP timestamp jumps
/// by the real gap and the first packet is marked, so the camera's jitter buffer starts fresh.</summary>
public sealed class OnvifBackchannelClient : ITalkSink
{
    public const string BackchannelRequire = "www.onvif.org/ver20/backchannel";
    private const int SamplesPerPacket = 160; // 20 ms at 8 kHz
    private static readonly TimeSpan GapForNewTalkspurt = TimeSpan.FromMilliseconds(200);

    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly RtspReader _reader;
    private readonly ILogger _logger;
    private readonly string _user;
    private readonly string _password;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private RtspAuth? _auth;
    private int _cseq;
    private string? _session;
    private string _sessionUri = "";
    private int _channel;
    private ushort _seq;
    private uint _timestamp;
    private readonly uint _ssrc = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
    private readonly byte[] _pending = new byte[SamplesPerPacket * 2];
    private int _pendingBytes;
    private readonly Stopwatch _sinceLastPacket = new();
    private bool _nextMarker = true;
    private Task? _readLoop;
    private Task? _keepAlive;

    public BackchannelTrack Track { get; private set; } = null!;

    /// <summary>"PCMU" or "PCMA" — what the camera asked for.</summary>
    public string Codec => Track.ALaw ? "PCMA" : "PCMU";

    private OnvifBackchannelClient(TcpClient tcp, Stream stream, string user, string password, ILogger logger)
    {
        _tcp = tcp;
        _stream = stream;
        _reader = new RtspReader(stream);
        _user = user;
        _password = password;
        _logger = logger;
    }

    /// <summary>Opens the backchannel on <paramref name="rtspUri"/> (the camera's stream URI, without
    /// credentials — they're passed separately and only ever sent as an auth header).</summary>
    public static async Task<OnvifBackchannelClient> ConnectAsync(Uri rtspUri, string? user, string? password,
        ILogger logger, CancellationToken ct)
    {
        if (!string.Equals(rtspUri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
            throw new BackchannelException($"Two-way audio needs an rtsp:// stream URI (got {rtspUri.Scheme}://).");

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(rtspUri.Host, rtspUri.Port > 0 ? rtspUri.Port : 554, connectTimeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new BackchannelException($"Couldn't connect to the camera at {rtspUri.Host}.", ex);
        }

        var client = new OnvifBackchannelClient(tcp, tcp.GetStream(), user ?? "", password ?? "", logger);
        try
        {
            await client.HandshakeAsync(rtspUri, ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private async Task HandshakeAsync(Uri rtspUri, CancellationToken ct)
    {
        var uri = rtspUri.GetLeftPart(UriPartial.Query);
        var describe = await RequestAsync("DESCRIBE", uri, ct, ("Accept", "application/sdp"), ("Require", BackchannelRequire));
        if (describe.Status == 551)
            throw new BackchannelException("This camera doesn't support two-way audio (it rejected the ONVIF audio backchannel).");
        EnsureOk(describe, "DESCRIBE");

        var baseUri = new Uri(describe.Header("Content-Base") ?? describe.Header("Content-Location") ?? uri);
        Track = BackchannelSdp.Parse(describe.Body, baseUri)
            ?? throw new BackchannelException("The camera didn't offer its speaker on this stream. In the camera's own settings, check that audio output (two-way talk) is enabled and set to G.711.");

        var setup = await RequestAsync("SETUP", Track.ControlUri, ct,
            ("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"), ("Require", BackchannelRequire));
        EnsureOk(setup, "SETUP");
        var sessionHeader = setup.Header("Session") ?? throw new BackchannelException("The camera didn't return an RTSP session.");
        _session = sessionHeader.Split(';')[0].Trim();
        var timeoutSeconds = ParseSessionTimeout(sessionHeader) ?? 60;
        _channel = ParseInterleavedChannel(setup.Header("Transport")) ?? 0;

        // Aggregate control for PLAY/OPTIONS/TEARDOWN is the base URI.
        _sessionUri = baseUri.ToString();
        var play = await RequestAsync("PLAY", _sessionUri, ct, ("Range", "npt=0.000-"), ("Require", BackchannelRequire));
        EnsureOk(play, "PLAY");

        _readLoop = Task.Run(() => DrainAsync(_cts.Token));
        var interval = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds / 2, 5, 25));
        _keepAlive = Task.Run(() => KeepAliveAsync(interval, _cts.Token));
        _logger.LogInformation("Audio backchannel open to {Host}: {Codec}, interleaved channel {Channel}.",
            new Uri(_sessionUri).Host, Codec, _channel);
    }

    private static void EnsureOk(RtspResponse r, string method)
    {
        if (r.Status == 401) throw new BackchannelException("The camera rejected the username or password for two-way audio.");
        if (r.Status is < 200 or >= 300)
            throw new BackchannelException($"The camera refused two-way audio ({method} returned {r.Status} {r.Reason}).");
    }

    internal static int? ParseSessionTimeout(string sessionHeader)
    {
        foreach (var part in sessionHeader.Split(';').Skip(1))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && kv[0].Equals("timeout", StringComparison.OrdinalIgnoreCase) && int.TryParse(kv[1], out var t)) return t;
        }
        return null;
    }

    internal static int? ParseInterleavedChannel(string? transport)
    {
        if (transport is null) return null;
        foreach (var part in transport.Split(';'))
        {
            var kv = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (kv.Length == 2 && kv[0].Equals("interleaved", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(kv[1].Split('-')[0], out var ch)) return ch;
        }
        return null;
    }

    // ── Audio ───────────────────────────────────────────────────────────────

    /// <summary>Queues PCM for the camera, sending every complete 20 ms packet now and holding any
    /// remainder for the next call.</summary>
    public async Task SendPcmAsync(ReadOnlyMemory<byte> pcm16le, CancellationToken ct)
    {
        var input = pcm16le;
        if (_sinceLastPacket.IsRunning && _sinceLastPacket.Elapsed > GapForNewTalkspurt)
        {
            // A new talkspurt: drop any half packet left from the last one and move the timestamp on
            // by the real silence, so the camera doesn't try to play the two back to back.
            _pendingBytes = 0;
            _timestamp += (uint)(_sinceLastPacket.Elapsed.TotalSeconds * Track.ClockRate);
            _nextMarker = true;
        }

        while (input.Length > 0)
        {
            var take = Math.Min(input.Length, _pending.Length - _pendingBytes);
            input.Span[..take].CopyTo(_pending.AsSpan(_pendingBytes));
            _pendingBytes += take;
            input = input[take..];
            if (_pendingBytes == _pending.Length)
            {
                await SendPacketAsync(_pending, ct);
                _pendingBytes = 0;
            }
        }
    }

    private async Task SendPacketAsync(byte[] pcm, CancellationToken ct)
    {
        var frame = new byte[4 + 12 + SamplesPerPacket];
        frame[0] = (byte)'$';
        frame[1] = (byte)_channel;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 12 + SamplesPerPacket);
        BuildRtpHeader(frame.AsSpan(4, 12), Track.PayloadType, _nextMarker, _seq++, _timestamp, _ssrc);
        G711.Encode(pcm, frame.AsSpan(16), Track.ALaw);
        _timestamp += SamplesPerPacket;
        _nextMarker = false;

        await WriteAsync(frame, ct);
        _sinceLastPacket.Restart();
    }

    /// <summary>Nothing to do on release: the RTSP session stays open between presses, and the next
    /// press starts a new talkspurt (see SendPcmAsync).</summary>
    public Task EndTalkspurtAsync(CancellationToken ct) => Task.CompletedTask;

    internal static void BuildRtpHeader(Span<byte> h, int payloadType, bool marker, ushort seq, uint timestamp, uint ssrc)
    {
        h[0] = 0x80; // version 2, no padding/extension/CSRC
        h[1] = (byte)((marker ? 0x80 : 0) | (payloadType & 0x7F));
        BinaryPrimitives.WriteUInt16BigEndian(h[2..], seq);
        BinaryPrimitives.WriteUInt32BigEndian(h[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], ssrc);
    }

    // ── RTSP plumbing ───────────────────────────────────────────────────────

    private async Task<RtspResponse> RequestAsync(string method, string uri, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        var response = await SendRequestAsync(method, uri, headers, ct);
        if (response.Status == 401 && _auth is null && _user.Length > 0)
        {
            _auth = RtspAuth.FromChallenges(response.HeaderValues("WWW-Authenticate"), _user, _password);
            if (_auth is not null) response = await SendRequestAsync(method, uri, headers, ct);
        }
        return response;
    }

    private async Task<RtspResponse> SendRequestAsync(string method, string uri, (string Name, string Value)[] headers, CancellationToken ct)
    {
        var cseq = await WriteRequestAsync(method, uri, headers, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (true)
            {
                var r = await _reader.ReadResponseAsync(timeout.Token);
                if (r.CSeq is null || r.CSeq == cseq) return r;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new BackchannelException($"The camera didn't answer {method} in time.");
        }
        catch (IOException ex)
        {
            throw new BackchannelException("The camera closed the connection.", ex);
        }
    }

    private async Task<int> WriteRequestAsync(string method, string uri, (string Name, string Value)[] headers, CancellationToken ct)
    {
        var cseq = Interlocked.Increment(ref _cseq);
        var sb = new StringBuilder();
        sb.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
        sb.Append("CSeq: ").Append(cseq).Append("\r\n");
        sb.Append("User-Agent: LarisVMS\r\n");
        if (_auth is not null) sb.Append("Authorization: ").Append(_auth.Header(method, uri)).Append("\r\n");
        if (_session is not null) sb.Append("Session: ").Append(_session).Append("\r\n");
        foreach (var (name, value) in headers) sb.Append(name).Append(": ").Append(value).Append("\r\n");
        sb.Append("\r\n");
        await WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        return cseq;
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _stream.WriteAsync(bytes, ct); }
        catch (IOException ex) { throw new BackchannelException("The camera closed the two-way audio connection.", ex); }
        finally { _writeLock.Release(); }
    }

    /// <summary>After PLAY, everything the camera sends (RTCP, keepalive replies) is read and dropped,
    /// so its socket buffer never fills and stalls the connection.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await _reader.ReadResponseAsync(ct);
                if (r.Status is < 200 or >= 300)
                    _logger.LogDebug("Audio backchannel keepalive answered {Status} {Reason}.", r.Status, r.Reason);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private async Task KeepAliveAsync(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct);
                await WriteRequestAsync("OPTIONS", _sessionUri, [], ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or BackchannelException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null && _tcp.Connected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await WriteRequestAsync("TEARDOWN", _sessionUri, [], timeout.Token);
            }
            catch (Exception) { /* best effort — the camera drops the session when the socket closes anyway */ }
        }
        _cts.Cancel();
        _tcp.Dispose();
        try { if (_readLoop is not null) await _readLoop; } catch { /* already logged/ignored */ }
        try { if (_keepAlive is not null) await _keepAlive; } catch { }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>One RTSP response.</summary>
internal sealed class RtspResponse(int status, string reason, List<(string Name, string Value)> headers, string body)
{
    public int Status { get; } = status;
    public string Reason { get; } = reason;
    public string Body { get; } = body;

    public string? Header(string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public IEnumerable<string> HeaderValues(string name) =>
        headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
    public int? CSeq => int.TryParse(Header("CSeq"), out var c) ? c : null;
}

/// <summary>Reads RTSP responses off a connection that may also carry interleaved binary frames
/// ('$', channel, 16-bit length, payload), skipping those.</summary>
internal sealed class RtspReader(Stream stream)
{
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    private async ValueTask<byte> ReadByteAsync(CancellationToken ct)
    {
        if (_start == _end)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, ct);
            if (_end == 0) throw new IOException("Connection closed.");
        }
        return _buffer[_start++];
    }

    private async ValueTask SkipAsync(int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++) await ReadByteAsync(ct);
    }

    private async ValueTask<string> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = await ReadByteAsync(ct);
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            if (sb.Length > 8192) throw new IOException("RTSP header line too long.");
            sb.Append((char)b);
        }
    }

    public async Task<RtspResponse> ReadResponseAsync(CancellationToken ct)
    {
        string statusLine;
        while (true)
        {
            var first = await ReadByteAsync(ct);
            if (first == '$')
            {
                await ReadByteAsync(ct); // channel
                var len = (await ReadByteAsync(ct) << 8) | await ReadByteAsync(ct);
                await SkipAsync(len, ct);
                continue;
            }
            if (first is (byte)'\r' or (byte)'\n') continue;
            statusLine = (char)first + await ReadLineAsync(ct);
            break;
        }

        var parts = statusLine.Split(' ', 3);
        // HTTP too: DahuaAudioPostClient reads its challenge response with this same reader.
        if (parts.Length < 2 || !(parts[0].StartsWith("RTSP/", StringComparison.Ordinal) || parts[0].StartsWith("HTTP/", StringComparison.Ordinal))
            || !int.TryParse(parts[1], out var status))
            throw new IOException($"Unexpected status line: {statusLine}");

        var headers = new List<(string, string)>();
        while (true)
        {
            var line = await ReadLineAsync(ct);
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon > 0) headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        var body = "";
        var lengthHeader = headers.FirstOrDefault(h => h.Item1.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Item2;
        if (int.TryParse(lengthHeader, out var length) && length > 0)
        {
            var bytes = new byte[Math.Min(length, 1 << 20)];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = await ReadByteAsync(ct);
            if (length > bytes.Length) await SkipAsync(length - bytes.Length, ct);
            body = Encoding.UTF8.GetString(bytes);
        }
        return new RtspResponse(status, parts.Length > 2 ? parts[2] : "", headers, body);
    }
}
