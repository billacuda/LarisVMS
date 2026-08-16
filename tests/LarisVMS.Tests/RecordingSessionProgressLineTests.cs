using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>Covers parsing real-time fps out of ffmpeg's own periodic progress line (M11 health
/// signals) — the same real captured line RecordingSessionStreamResolutionTests already uses as a
/// "not a video stream line" negative case — and deriving bitrate from a completed segment's actual
/// size/duration (EstimateBitrateKbps), used instead of the progress line's own bitrate field, which
/// (see the regressed-but-now-fixed 0.7x bug: dashboard fps/bitrate/last-report staying blank despite
/// active recording) is never a real number under this app's own `-f tee` pipeline — confirmed
/// against a real captured run of BuildTeeOutputs's exact tee spec, which prints "bitrate=N/A" for the
/// entire session, not just the first tick or two the way an ordinary single-output mux briefly does.</summary>
public class RecordingSessionProgressLineTests
{
    [Fact]
    public void ParsesFpsFromARealCapturedProgressLineWithABitrateNumber()
    {
        // Not this app's own pipeline shape (tee never reports a real bitrate — see class doc
        // comment) but a general single-output ffmpeg run can, and the parser should still pick it up
        // when it's there.
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
    public void ParsesFpsEvenWhenBitrateIsNA()
    {
        // This is the normal, permanent case for this app's own tee pipeline (see class doc
        // comment) — not a rare early-startup blip. Fps must still come through; BitrateKbps is null,
        // not a reason to reject the whole line the way the original regex (requiring a numeric
        // bitrate to match at all) used to.
        var line = "frame=   3 fps=12.0 q=-1.0 size=N/A time=00:00:00.30 bitrate=N/A speed=   1x";

        var result = RecordingSession.TryParseProgressLine(line);

        Assert.NotNull(result);
        Assert.Equal(12, result!.Value.Fps);
        Assert.Null(result.Value.BitrateKbps);
    }

    [Fact]
    public void EstimatesBitrateFromSegmentSizeAndDuration()
    {
        // 1,000,000 bytes over 8 seconds = 1,000,000 * 8 / 1000 / 8 = 1000 kbit/s.
        var bitrateKbps = RecordingSession.EstimateBitrateKbps(1_000_000, TimeSpan.FromSeconds(8));

        Assert.Equal(1000, bitrateKbps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void EstimateBitrateReturnsNullForADegenerateDuration(int seconds)
    {
        Assert.Null(RecordingSession.EstimateBitrateKbps(1_000_000, TimeSpan.FromSeconds(seconds)));
    }
}
