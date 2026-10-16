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

    // ── Re-encoding in the camera's own codec ──────────────────────────────

    [Theory]
    [InlineData("hevc", "hevc_nvenc")]
    [InlineData("H265", "hevc_nvenc")]
    [InlineData("H264", "h264_nvenc")]
    [InlineData(null, "h264_nvenc")]
    public void KeepsTheCamerasCodec(string? sourceCodec, string expected)
    {
        Assert.Equal(expected, EncoderSelection.ChooseEncoder(["libx264", "libx265", "h264_nvenc", "hevc_nvenc"], sourceCodec));
    }

    [Fact]
    public void AnH265CameraFallsBackToH264OnlyWithoutAnyH265Encoder()
    {
        Assert.Equal("libx265", EncoderSelection.ChooseEncoder(["libx264", "libx265", "h264_nvenc"], "hevc"));
        Assert.Equal("h264_qsv", EncoderSelection.ChooseEncoder(["libx264", "h264_qsv"], "hevc"));
    }

    [Fact]
    public void TargetsTheCamerasConfiguredBitrate()
    {
        Assert.Equal(3840, EncoderSelection.TargetBitrateKbps("hevc_nvenc", "hevc", 3840, 4096, 1856, 15));
        // Same picture as H.264 needs more bits.
        Assert.Equal(5760, EncoderSelection.TargetBitrateKbps("h264_nvenc", "hevc", 3840, 4096, 1856, 15));
    }

    [Fact]
    public void EstimatesABitrateWhenTheCameraDoesNotReportOne()
    {
        // 4096x1856 at 15 fps in H.265 — the real camera this was tuned on runs at about 5 Mbit/s.
        Assert.InRange(EncoderSelection.TargetBitrateKbps("hevc_nvenc", "hevc", null, 4096, 1856, 15), 4500, 5500);
        Assert.Equal(500, EncoderSelection.TargetBitrateKbps("h264_nvenc", "h264", null, 320, 240, 5)); // floor
        Assert.Equal(20000, EncoderSelection.TargetBitrateKbps("h264_nvenc", "h264", 90000, null, null, null)); // ceiling
    }
}
