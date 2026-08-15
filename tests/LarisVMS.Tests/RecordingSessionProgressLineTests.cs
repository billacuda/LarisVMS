using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>Covers parsing real-time fps/bitrate out of ffmpeg's own periodic progress line (M11
/// health signals) — the same real captured line RecordingSessionStreamResolutionTests already uses
/// as a "not a video stream line" negative case.</summary>
public class RecordingSessionProgressLineTests
{
    [Fact]
    public void ParsesARealCapturedProgressLine()
    {
        var line = "frame=  123 fps= 15 q=-1.0 size=    2048kB time=00:00:08.20 bitrate=2047.0kbits/s speed=   1x";

        var result = RecordingSession.TryParseProgressLine(line);

        Assert.NotNull(result);
        Assert.Equal(15, result!.Value.Fps);
        Assert.Equal(2047, result.Value.BitrateKbps);
    }

    [Fact]
    public void TruncatesFractionalFpsAndBitrateToWholeNumbers()
    {
        var line = "frame= 9001 fps= 14.9 q=-1.0 size=   10240kB time=00:01:23.45 bitrate= 987.6kbits/s speed=1.0x";

        var result = RecordingSession.TryParseProgressLine(line);

        Assert.NotNull(result);
        Assert.Equal(14, result!.Value.Fps);
        Assert.Equal(987, result.Value.BitrateKbps);
    }

    [Theory]
    [InlineData("    Stream #0:0: Video: hevc (Main), yuv420p(tv, bt709), 2560x1440, 15 fps, 15 tbr, 90k tbn")]
    [InlineData("Input #0, rtsp, from 'rtsp://***:***@192.168.86.221:554/cam/realmonitor?channel=1&subtype=0':")]
    [InlineData("")]
    public void IgnoresNonProgressLines(string line)
    {
        Assert.Null(RecordingSession.TryParseProgressLine(line));
    }

    [Fact]
    public void IgnoresAProgressLineWithNoBitrateYet()
    {
        // ffmpeg prints "bitrate=N/A" for the first tick or two before it has enough data —
        // TryParseProgressLine must not throw on that, just report no measurement yet.
        var line = "frame=   3 fps=0.0 q=-1.0 size=       0kB time=00:00:00.00 bitrate=N/A speed=   0x";

        Assert.Null(RecordingSession.TryParseProgressLine(line));
    }
}
