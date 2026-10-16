namespace LarisVMS.Media;

/// <summary>Browsers only play AAC audio in the fMP4 that live view and playback feed to Media Source
/// Extensions — a camera sending G.711 (pcm_alaw/pcm_mulaw, common on cameras with a microphone) or
/// G.726 made its tile fail to play at all. Such a camera's audio is re-encoded to AAC as it's
/// recorded; video is still copied untouched, and the audio encode is a few percent of one core.</summary>
public static class AudioTranscode
{
    /// <summary>True for any audio codec other than AAC, as ffmpeg names it ("aac", "aac (LC)", …).
    /// Unknown (null — the camera hasn't been connected to yet) copies, as before; once the first
    /// connection reports the codec, the recorder restarts with transcoding if it needs it.</summary>
    public static bool NeedsAac(string? audioCodec) =>
        !string.IsNullOrWhiteSpace(audioCodec) && !audioCodec.TrimStart().StartsWith("aac", StringComparison.OrdinalIgnoreCase);

    /// <summary>32 kbit/s is plenty for a camera microphone (usually 8–16 kHz mono) and within what
    /// ffmpeg's AAC encoder accepts at those sample rates.</summary>
    public static IReadOnlyList<string> AacArgs { get; } = ["-c:a", "aac", "-b:a", "32k"];
}
