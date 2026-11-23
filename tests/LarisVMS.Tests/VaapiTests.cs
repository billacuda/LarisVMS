using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>VAAPI is how Intel and AMD GPUs encode on Linux: the render node is chosen by vendor, the
/// encoders are only offered when one exists, and the arguments open the device and upload frames.</summary>
public sealed class VaapiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larisvms-vaapi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void AddRenderNode(string name, string vendor)
    {
        var device = Path.Combine(_root, "drm", name, "device");
        Directory.CreateDirectory(device);
        File.WriteAllText(Path.Combine(device, "vendor"), vendor + "\n");
    }

    [Fact]
    public void PicksTheIntelOrAmdRenderNodeNotTheNvidiaOne()
    {
        AddRenderNode("renderD128", "0x10de");
        AddRenderNode("renderD129", "0x8086");

        Assert.Equal(Path.Combine("/dev/dri", "renderD129"), VaapiDevice.Find(Path.Combine(_root, "drm"), "/dev/dri"));
    }

    [Fact]
    public void NoIntelOrAmdGpuMeansNoDevice()
    {
        AddRenderNode("renderD128", "0x10de");

        Assert.Null(VaapiDevice.Find(Path.Combine(_root, "drm"), "/dev/dri"));
        Assert.Null(VaapiDevice.Find(Path.Combine(_root, "missing"), "/dev/dri"));
    }

    [Fact]
    public void VaapiEncodersAreHiddenWithoutADevice()
    {
        IReadOnlyList<string> listed = ["libx264", "h264_vaapi", "hevc_vaapi"];

        Assert.Equal(["libx264"], FfmpegCapabilityProber.WithoutUnusableVaapi(listed, haveVaapiDevice: false));
        Assert.Equal(listed, FfmpegCapabilityProber.WithoutUnusableVaapi(listed, haveVaapiDevice: true));
    }

    /// <summary>ffmpeg lists NVENC, QSV, and AMF even on a machine with no GPU (Ubuntu's ffmpeg on a
    /// VM did); choosing one there failed privacy masking and export conversion.</summary>
    [Fact]
    public void HardwareEncodersNeedTheirGpu()
    {
        IReadOnlyList<string> listed = ["libx264", "libx265", "h264_qsv", "hevc_qsv", "h264_nvenc", "hevc_nvenc", "h264_amf"];

        Assert.Equal(["libx264", "libx265"], FfmpegCapabilityProber.WithoutMissingHardware(listed, nvidia: false, intel: false, amd: false));
        Assert.Equal(["libx264", "libx265", "h264_nvenc", "hevc_nvenc"],
            FfmpegCapabilityProber.WithoutMissingHardware(listed, nvidia: true, intel: false, amd: false));
        Assert.Equal("libx264", EncoderSelection.ChooseH264Encoder(
            FfmpegCapabilityProber.WithoutMissingHardware(listed, nvidia: false, intel: false, amd: false)));
        Assert.Equal(["libx264", "h264_qsv", "h264_amf"],
            FfmpegCapabilityProber.WithoutMissingHardware(["libx264", "h264_qsv", "h264_amf"], nvidia: false, intel: true, amd: true));
    }

    [Fact]
    public void BuildArgsOpensTheDeviceAndUploadsAfterTheSoftwareFilters()
    {
        var args = EncodePipeline.BuildArgs(
            new EncodePipelineOptions("h264_vaapi", VideoFilters: ["drawbox=x=0:y=0:w=10:h=10:color=black:t=fill"], BitrateKbps: 4000),
            "/dev/dri/renderD129");

        Assert.Equal(
            ["-vaapi_device", "/dev/dri/renderD129",
             "-vf", "drawbox=x=0:y=0:w=10:h=10:color=black:t=fill,format=nv12,hwupload",
             "-c:v", "h264_vaapi", "-b:v", "4000k"],
            args);
        Assert.Null(EncodePipeline.RealtimePreset("h264_vaapi"));
        Assert.Equal(EncoderFamily.Vaapi, EncoderFamilies.For("hevc_vaapi"));
    }

    [Fact]
    public void SelectionPrefersNvencThenVaapi()
    {
        Assert.Equal("h264_vaapi", EncoderSelection.ChooseH264Encoder(["libx264", "h264_qsv", "h264_vaapi"]));
        Assert.Equal("h264_nvenc", EncoderSelection.ChooseH264Encoder(["h264_vaapi", "h264_nvenc"]));
        Assert.Equal("hevc_vaapi", EncoderSelection.ChooseEncoder(["libx265", "hevc_vaapi", "h264_vaapi"], "hevc"));
    }
}
