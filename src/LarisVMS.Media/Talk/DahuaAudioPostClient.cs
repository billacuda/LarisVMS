using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Media.Talk;

/// <summary>Two-way audio for Dahua-family cameras (Dahua, Amcrest, Lorex, EmpireTech) whose firmware
/// advertises an audio output over ONVIF but doesn't implement the RTSP backchannel — the fallback
/// when <see cref="OnvifBackchannelClient"/> is refused. Uses Dahua's HTTP API: a POST to
/// <c>/cgi-bin/audio.cgi?action=postAudio&amp;httptype=singlepart&amp;channel=1</c> with
/// <c>Content-Type: Audio/G.711A</c> and an oversized Content-Length, whose body is raw A-law audio
/// written as it's spoken; the camera plays what arrives and stops when the connection closes.
///
/// One POST per press. The body is a live stream that can't be replayed, so the digest challenge is
/// fetched first with an empty POST and the real request carries the Authorization up front.</summary>
public sealed class DahuaAudioPostClient : ITalkSink
{
    private const string PathAndQuery = "/cgi-bin/audio.cgi?action=postAudio&httptype=singlepart&channel=1";
    private const int StreamContentLength = 99_999_999; // "until the connection closes" — what Dahua's own clients send

    private readonly Uri _baseUri;
    private readonly string _user;
    private readonly string _password;
    private readonly ILogger _logger;
    private Stream? _stream;
    private byte[] _encoded = new byte[1024];

    public string Codec => "PCMA";

    private DahuaAudioPostClient(Uri baseUri, string? user, string? password, ILogger logger)
    {
        _baseUri = baseUri;
        _user = user ?? "";
        _password = password ?? "";
        _logger = logger;
    }

    /// <summary>Checks the camera answers the audio API (with these credentials) before reporting the
    /// speaker ready; the streaming connection itself opens on the first press.</summary>
    public static async Task<DahuaAudioPostClient> ConnectAsync(Uri baseUri, string? user, string? password, ILogger logger, CancellationToken ct)
    {
        var client = new DahuaAudioPostClient(baseUri, user, password, logger);
        var auth = await client.ChallengeAsync(ct);
        if (auth is null && client._user.Length > 0)
            logger.LogDebug("Dahua audio API at {Host} didn't ask for credentials.", baseUri.Host);
        return client;
    }

    public async Task SendPcmAsync(ReadOnlyMemory<byte> pcm16le, CancellationToken ct)
    {
        if (_stream is null) await OpenStreamAsync(ct);
        var samples = pcm16le.Length / 2;
        if (_encoded.Length < samples) _encoded = new byte[samples];
        G711.Encode(pcm16le.Span, _encoded, aLaw: true);
        try
        {
            await _stream!.WriteAsync(_encoded.AsMemory(0, samples), ct);
        }
        catch (IOException ex)
        {
            Close();
            throw new BackchannelException("The camera closed the audio connection (check the camera's audio output and the user's permissions).", ex);
        }
    }

    public Task EndTalkspurtAsync(CancellationToken ct)
    {
        Close();
        return Task.CompletedTask;
    }

    private async Task OpenStreamAsync(CancellationToken ct)
    {
        var auth = await ChallengeAsync(ct);
        _stream = await ConnectStreamAsync(ct);
        var sb = new StringBuilder();
        sb.Append("POST ").Append(PathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(_baseUri.Authority).Append("\r\n");
        if (auth is not null) sb.Append("Authorization: ").Append(auth.Header("POST", PathAndQuery)).Append("\r\n");
        sb.Append("Content-Type: Audio/G.711A\r\n");
        sb.Append("Content-Length: ").Append(StreamContentLength).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
    }

    /// <summary>An empty POST to the audio API: a 401 carries the digest challenge for the real one.</summary>
    private async Task<RtspAuth?> ChallengeAsync(CancellationToken ct)
    {
        await using var stream = await ConnectStreamAsync(ct);
        var request = $"POST {PathAndQuery} HTTP/1.1\r\nHost: {_baseUri.Authority}\r\nContent-Type: Audio/G.711A\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);

        RtspResponse response;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { response = await new RtspReader(stream).ReadResponseAsync(timeout.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                throw new BackchannelException("The camera didn't answer its audio API.", ex);
            }
        }

        if (response.Status == 404)
            throw new BackchannelException("This camera doesn't support two-way audio (no audio API).");
        if (response.Status != 401) return null;
        return RtspAuth.FromChallenges(response.HeaderValues("WWW-Authenticate"), _user, _password)
            ?? throw new BackchannelException("The camera asked for a sign-in method LarisVMS doesn't support for two-way audio.");
    }

    private async Task<Stream> ConnectStreamAsync(CancellationToken ct)
    {
        var https = string.Equals(_baseUri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(_baseUri.Host, _baseUri.Port > 0 ? _baseUri.Port : https ? 443 : 80, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new BackchannelException($"Couldn't connect to the camera at {_baseUri.Host}.", ex);
        }

        Stream stream = tcp.GetStream();
        if (https)
        {
            // Cameras on the LAN almost always present a self-signed certificate; the camera's
            // credentials are what authenticate it here, same as the event integration.
            var ssl = new SslStream(stream, false, (_, _, _, _) => true);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _baseUri.Host }, ct);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                await ssl.DisposeAsync();
                throw new BackchannelException($"Couldn't open a secure connection to the camera at {_baseUri.Host}.", ex);
            }
            stream = ssl;
        }

        // The stream owns the socket (TcpClient.GetStream / SslStream without leaveInnerStreamOpen),
        // so disposing the stream is what closes the connection.
        return stream;
    }

    private void Close()
    {
        try { _stream?.Dispose(); } catch (IOException) { }
        _stream = null;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
