namespace LarisVMS.Media.Talk;

/// <summary>Somewhere an operator's voice goes: a camera's speaker, reached either over the ONVIF RTSP
/// audio backchannel (<see cref="OnvifBackchannelClient"/>) or a vendor API
/// (<see cref="DahuaAudioPostClient"/>). Audio in is 16-bit little-endian PCM at 8 kHz mono.</summary>
public interface ITalkSink : IAsyncDisposable
{
    /// <summary>"PCMU" or "PCMA" — what the camera takes.</summary>
    string Codec { get; }

    Task SendPcmAsync(ReadOnlyMemory<byte> pcm16le, CancellationToken ct);

    /// <summary>Push-to-talk released. A sink that holds a per-press connection closes it here.</summary>
    Task EndTalkspurtAsync(CancellationToken ct);
}
