using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Pass 0 of the detection/hardware-acceleration overhaul: MotionSession.BuildFfmpegArgs/
/// ComputeCaptureFrameSize, covering the CUDA-vs-software decode split the same way
/// RecordingSessionPrivacyMaskTests covers RecordingSession.BuildDecodeArgs/BuildCodecArgs — pure
/// argument/arithmetic assertions, no ffmpeg process involved.
/// </summary>
public class MotionSessionFfmpegArgsTests
{
    private static MotionSessionOptions Options(string? hwaccel = null) =>
        new("ffmpeg", "rtsp://camera/sub", HardwareAcceleration: hwaccel);

    [Fact]
    public void NoAcceleratorKeepsThePlainSoftwareDecodePath()
    {
        var args = MotionSession.BuildFfmpegArgs(Options());

        Assert.Equal([
            "-nostdin", "-rtsp_transport", "tcp", "-timeout", "5000000",
            "-i", "rtsp://camera/sub",
            "-vf", "fps=5,scale=320:240,format=gray",
            "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"
        ], args);
    }

    [Theory]
    [InlineData("qsv")]
    [InlineData("amf")]
    [InlineData("")]
    public void OnlyCudaGetsTheGpuHybridPath(string hwaccel)
    {
        var args = MotionSession.BuildFfmpegArgs(Options(hwaccel)).ToList();
        var vfIndex = args.IndexOf("-vf");

        Assert.DoesNotContain("-hwaccel", args);
        Assert.Contains("format=gray", args[vfIndex + 1]); // still the plain -vf filter
    }

    [Fact]
    public void CudaUsesScaleCudaWithHwdownloadNotAPlainCpuScale()
    {
        var args = MotionSession.BuildFfmpegArgs(Options("cuda"));

        Assert.Equal([
            "-nostdin", "-rtsp_transport", "tcp", "-timeout", "5000000",
            "-hwaccel", "cuda", "-hwaccel_output_format", "cuda",
            "-i", "rtsp://camera/sub",
            "-vf", "fps=5,scale_cuda=w=320:h=240:format=nv12,hwdownload,format=nv12",
            "-fps_mode", "passthrough",
            "-f", "rawvideo", "-pix_fmt", "nv12", "pipe:1"
        ], args);
    }

    [Fact]
    public void CudaIsCaseInsensitive()
    {
        var args = MotionSession.BuildFfmpegArgs(Options("CUDA"));
        Assert.Contains("-hwaccel", args);
    }

    // Confirmed live (real Intel/NVIDIA hardware): pairing hwaccel decode with a CPU-only filter
    // broke privacy-mask burn-in outright (v0.112.0) and left it disabled to this day. This pass's
    // CUDA branch must never regress into that same shape — scale_cuda's own GPU filter is what
    // avoids it, so a plain "-vf ...,format=gray" (CPU) must never appear alongside "-hwaccel cuda".
    [Fact]
    public void CudaNeverPairsHwaccelDecodeWithAPlainCpuFilter()
    {
        var args = MotionSession.BuildFfmpegArgs(Options("cuda"));
        var vfIndex = args.ToList().IndexOf("-vf");

        Assert.Contains("-hwaccel", args);
        Assert.Contains("scale_cuda", args[vfIndex + 1]);
        Assert.DoesNotContain("format=gray", args[vfIndex + 1]); // hwdownload,format=nv12 only
    }

    [Fact]
    public void SoftwarePathFrameSizeIsJustTheYPlane()
    {
        Assert.Equal(320 * 240, MotionSession.ComputeCaptureFrameSize(Options()));
    }

    [Fact]
    public void CudaPathFrameSizeIncludesTheNv12ChromaPlane()
    {
        // 4:2:0 chroma subsampling: interleaved UV plane is half the Y plane's size.
        var yPlane = 320 * 240;
        Assert.Equal(yPlane + yPlane / 2, MotionSession.ComputeCaptureFrameSize(Options("cuda")));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("qsv", false)]
    [InlineData("amf", false)]
    [InlineData("cuda", true)]
    [InlineData("Cuda", true)]
    [InlineData("CUDA", true)]
    public void UsesNv12OnlyForCuda(string? hwaccel, bool expected)
    {
        Assert.Equal(expected, MotionSession.UsesNv12(hwaccel));
    }
}
