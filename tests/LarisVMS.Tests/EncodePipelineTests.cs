using LarisVMS.Media;

namespace LarisVMS.Tests;

public class EncodePipelineTests
{
    [Theory]
    [InlineData("h264_qsv", EncoderFamily.IntelQsv)]
    [InlineData("hevc_qsv", EncoderFamily.IntelQsv)]
    [InlineData("h264_nvenc", EncoderFamily.NvidiaNvenc)]
    [InlineData("hevc_nvenc", EncoderFamily.NvidiaNvenc)]
    [InlineData("h264_amf", EncoderFamily.AmdAmf)]
    [InlineData("hevc_amf", EncoderFamily.AmdAmf)]
    [InlineData("libx264", EncoderFamily.Software)]
    [InlineData("libx265", EncoderFamily.Software)]
    [InlineData("something_unknown", EncoderFamily.Software)]
    public void MapsEachKnownEncoderToItsFamily(string encoderName, EncoderFamily expected)
    {
        Assert.Equal(expected, EncoderFamilies.For(encoderName));
    }

    [Fact]
    public void QsvGetsTheQsvHwaccelFlag()
    {
        Assert.Equal(["-hwaccel", "qsv"], EncodePipeline.DecodeHwaccelArgs(EncoderFamily.IntelQsv));
    }

    [Fact]
    public void NvencGetsTheCudaHwaccelFlag()
    {
        Assert.Equal(["-hwaccel", "cuda"], EncodePipeline.DecodeHwaccelArgs(EncoderFamily.NvidiaNvenc));
    }

    [Fact]
    public void AmfAndSoftwareGetNoHwaccelFlag()
    {
        Assert.Empty(EncodePipeline.DecodeHwaccelArgs(EncoderFamily.AmdAmf));
        Assert.Empty(EncodePipeline.DecodeHwaccelArgs(EncoderFamily.Software));
    }

    [Fact]
    public void BuildArgsWithJustAnEncoderNameIsTheMinimalCommandLine()
    {
        var args = EncodePipeline.BuildArgs(new EncodePipelineOptions("libx264"));

        Assert.Equal(["-c:v", "libx264"], args);
    }

    [Fact]
    public void BuildArgsJoinsMultipleFiltersWithCommas()
    {
        var args = EncodePipeline.BuildArgs(new EncodePipelineOptions(
            "libx264", VideoFilters: ["scale=1280:-2", "drawbox=x=0:y=0:w=100:h=100:color=black:t=fill"]));

        Assert.Equal(["-vf", "scale=1280:-2,drawbox=x=0:y=0:w=100:h=100:color=black:t=fill", "-c:v", "libx264"], args);
    }

    [Fact]
    public void BuildArgsIncludesPresetCrfAndBitrateWhenGiven()
    {
        var args = EncodePipeline.BuildArgs(new EncodePipelineOptions(
            "libx264", Preset: "veryfast", Crf: 23, BitrateKbps: 4000));

        Assert.Equal(["-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-b:v", "4000k"], args);
    }

    [Fact]
    public void BuildArgsOmitsFilterFlagWhenTheListIsEmpty()
    {
        var args = EncodePipeline.BuildArgs(new EncodePipelineOptions("libx264", VideoFilters: []));

        Assert.DoesNotContain("-vf", args);
    }
}
