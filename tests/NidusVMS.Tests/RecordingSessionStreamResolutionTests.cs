using NidusVMS.Media;

namespace NidusVMS.Tests;

/// <summary>
/// Covers parsing real resolution/codec out of ffmpeg's own stderr — added because ONVIF's
/// advertised VideoEncoderConfiguration is unreliable (confirmed: Amcrest omits it entirely for
/// H.265 profiles), so this is now the source of truth for what a camera's Main stream actually is.
/// </summary>
public class RecordingSessionStreamResolutionTests
{
    [Fact]
    public void ParsesRealAmcrestH265StreamLine()
    {
        var line = "    Stream #0:0: Video: hevc (Main), yuv420p(tv, bt709), 2560x1440, 15 fps, 15 tbr, 90k tbn";

        var result = RecordingSession.TryParseVideoStreamLine(line);

        Assert.NotNull(result);
        Assert.Equal(2560, result!.Width);
        Assert.Equal(1440, result.Height);
        Assert.Equal("hevc", result.Codec);
    }

    [Fact]
    public void ParsesH264SubStreamLine()
    {
        var line = "    Stream #0:0: Video: h264 (High), yuvj420p(pc, bt709, progressive), 704x480, 25 fps, 25 tbr, 90k tbn";

        var result = RecordingSession.TryParseVideoStreamLine(line);

        Assert.NotNull(result);
        Assert.Equal(704, result!.Width);
        Assert.Equal(480, result.Height);
        Assert.Equal("h264", result.Codec);
    }

    [Theory]
    [InlineData("    Stream #0:1: Audio: aac (LC), 8000 Hz, mono, fltp, 32 kb/s")]
    [InlineData("Input #0, rtsp, from 'rtsp://***:***@192.168.86.221:554/cam/realmonitor?channel=1&subtype=0':")]
    [InlineData("frame=  123 fps= 15 q=-1.0 size=    2048kB time=00:00:08.20 bitrate=2047.0kbits/s speed=   1x")]
    public void IgnoresNonVideoStreamLines(string line)
    {
        Assert.Null(RecordingSession.TryParseVideoStreamLine(line));
    }
}
