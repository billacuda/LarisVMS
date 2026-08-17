using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Covers parsing the audio track's codec and sample rate out of ffmpeg's own stderr — the source
/// the dashboard's Audio column reads, for the same reason the video side parses stderr rather than
/// trusting ONVIF: this is what is actually arriving on the wire.
/// </summary>
public class RecordingSessionAudioStreamTests
{
    [Fact]
    public void ParsesAacStreamLine()
    {
        var line = "    Stream #0:1: Audio: aac (LC), 16000 Hz, mono, fltp, 32 kb/s";

        var result = RecordingSession.TryParseAudioStreamLine(line);

        Assert.NotNull(result);
        Assert.Equal("aac", result!.Codec);
        Assert.Equal(16000, result.SampleRateHz);
    }

    [Fact]
    public void ParsesG711StreamLine()
    {
        // G.711 µ-law/A-law at 8 kHz is what most of this fleet's cameras actually send.
        var line = "    Stream #0:1: Audio: pcm_alaw, 8000 Hz, mono, s16, 64 kb/s";

        var result = RecordingSession.TryParseAudioStreamLine(line);

        Assert.NotNull(result);
        Assert.Equal("pcm_alaw", result!.Codec);
        Assert.Equal(8000, result.SampleRateHz);
    }

    [Fact]
    public void ParsesStreamLineWithLanguageTagAndFractionalKilohertzRate()
    {
        var line = "  Stream #0:1(und): Audio: aac (LC) (mp4a / 0x6134706D), 44100 Hz, stereo, fltp, 128 kb/s";

        var result = RecordingSession.TryParseAudioStreamLine(line);

        Assert.NotNull(result);
        Assert.Equal("aac", result!.Codec);
        Assert.Equal(44100, result.SampleRateHz);
    }

    [Fact]
    public void ReportsCodecWithNullRateWhenTheLineCarriesNoHzFigure()
    {
        // The whole reason codec and rate are matched by two separate patterns: a line without a
        // rate must still report the codec rather than matching nothing and reporting no audio at
        // all — the failure mode the progress-line regex hit for real when it required a bitrate.
        var line = "    Stream #0:1: Audio: pcm_mulaw, mono, s16";

        var result = RecordingSession.TryParseAudioStreamLine(line);

        Assert.NotNull(result);
        Assert.Equal("pcm_mulaw", result!.Codec);
        Assert.Null(result.SampleRateHz);
    }

    [Fact]
    public void DoesNotMistakeABitRateFigureForASampleRate()
    {
        var line = "    Stream #0:1: Audio: aac (LC), 8000 Hz, mono, fltp, 32 kb/s";

        var result = RecordingSession.TryParseAudioStreamLine(line);

        Assert.Equal(8000, result!.SampleRateHz);
    }

    [Theory]
    [InlineData("    Stream #0:0: Video: hevc (Main), yuv420p(tv, bt709), 2560x1440, 15 fps, 15 tbr, 90k tbn")]
    [InlineData("Input #0, rtsp, from 'rtsp://***:***@192.168.86.221:554/cam/realmonitor?channel=1&subtype=0':")]
    [InlineData("frame=  123 fps= 15 q=-1.0 size=    2048kB time=00:00:08.20 bitrate=2047.0kbits/s speed=   1x")]
    public void IgnoresNonAudioStreamLines(string line)
    {
        Assert.Null(RecordingSession.TryParseAudioStreamLine(line));
    }
}
