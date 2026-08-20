using LarisVMS.Media;

namespace LarisVMS.Tests;

public class EncoderSelectionTests
{
    [Fact]
    public void PrefersNvencOverEverythingElse()
    {
        Assert.Equal("h264_nvenc", EncoderSelection.ChooseH264Encoder(["libx264", "h264_qsv", "h264_amf", "h264_nvenc"]));
    }

    [Fact]
    public void PrefersQsvOverAmfAndSoftwareWhenNvencIsAbsent()
    {
        Assert.Equal("h264_qsv", EncoderSelection.ChooseH264Encoder(["libx264", "h264_amf", "h264_qsv"]));
    }

    [Fact]
    public void PrefersAmfOverSoftwareWhenNeitherNvencNorQsvIsPresent()
    {
        Assert.Equal("h264_amf", EncoderSelection.ChooseH264Encoder(["libx264", "h264_amf"]));
    }

    [Fact]
    public void FallsBackToSoftwareWhenNoHardwareEncoderIsDetected()
    {
        Assert.Equal("libx264", EncoderSelection.ChooseH264Encoder(["libx264", "libx265"]));
    }

    [Fact]
    public void ReturnsNullWhenNothingWasDetectedAtAll()
    {
        Assert.Null(EncoderSelection.ChooseH264Encoder([]));
    }

    [Fact]
    public void IgnoresHevcOnlyHardwareEncodersSinceOutputIsAlwaysH264()
    {
        // Only hevc_nvenc detected, no h264_nvenc — this node's NVENC apparently only reported HEVC
        // support, so it doesn't count for this app's H.264-only transcode output.
        Assert.Equal("libx264", EncoderSelection.ChooseH264Encoder(["libx264", "hevc_nvenc"]));
    }
}
