using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Covers parsing real `ffmpeg -encoders` output — the M17 foundation for hardware-transcode
/// capability detection. Sample lines below are the real shape a Windows ffmpeg build with QSV/
/// NVENC/AMF support compiled in prints, trimmed to the handful this app actually looks for.
/// </summary>
public class FfmpegCapabilityProberTests
{
    private const string SampleOutput = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .....D = Frame-level multithreading
         ......
         V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
         V..... libx265              libx265 H.265 / HEVC (codec hevc)
         V..X.. h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
         V..X.. hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)
         V....D h264_qsv             H.264 / AVC (Intel Quick Sync Video acceleration) (codec h264)
         V....D hevc_qsv             HEVC (Intel Quick Sync Video acceleration) (codec hevc)
         A..... aac                  AAC (Advanced Audio Coding)
         V..... mjpeg                MJPEG (Motion JPEG)
        """;

    [Fact]
    public void FindsEveryKnownEncoderPresentInTheOutput()
    {
        var result = FfmpegCapabilityProber.ParseEncodersOutput(SampleOutput);

        Assert.Equal(["libx264", "libx265", "h264_qsv", "hevc_qsv", "h264_nvenc", "hevc_nvenc"], result);
    }

    [Fact]
    public void EncodersAmdAmfIsAbsentWhenNotInTheOutput()
    {
        var result = FfmpegCapabilityProber.ParseEncodersOutput(SampleOutput);

        Assert.DoesNotContain("h264_amf", result);
        Assert.DoesNotContain("hevc_amf", result);
    }

    [Fact]
    public void ResultOrderMatchesKnownEncodersRegardlessOfOutputOrder()
    {
        // hevc_nvenc listed before h264_qsv here, the reverse of SampleOutput above.
        const string reordered = """
             V..X.. hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)
             V....D h264_qsv             H.264 / AVC (Intel Quick Sync Video acceleration) (codec h264)
            """;

        var result = FfmpegCapabilityProber.ParseEncodersOutput(reordered);

        Assert.Equal(["h264_qsv", "hevc_nvenc"], result);
    }

    [Fact]
    public void EmptyOutputYieldsNoEncoders()
    {
        Assert.Empty(FfmpegCapabilityProber.ParseEncodersOutput(""));
    }

    [Fact]
    public void DoesNotSubstringMatchAnUnknownEncoderName()
    {
        const string line = " V....D h264_qsv2            some hypothetical future encoder (codec h264)";

        Assert.Empty(FfmpegCapabilityProber.ParseEncodersOutput(line));
    }

    [Fact]
    public void DetectsLibWebpWhenTheBuildListsIt()
    {
        const string withWebp = SampleOutput + "\n V..... libwebp              libwebp WebP (codec webp)";

        Assert.True(FfmpegCapabilityProber.ListsEncoder(withWebp, "libwebp"));
    }

    [Fact]
    public void ReportsNoLibWebpWhenTheBuildOmitsIt()
    {
        Assert.False(FfmpegCapabilityProber.ListsEncoder(SampleOutput, "libwebp"));
    }

    [Fact]
    public void LibWebpMatchIsExactNotSubstring()
    {
        const string line = " V..... libwebp_anim         some hypothetical animated variant (codec webp)";

        Assert.False(FfmpegCapabilityProber.ListsEncoder(line, "libwebp"));
    }
}
