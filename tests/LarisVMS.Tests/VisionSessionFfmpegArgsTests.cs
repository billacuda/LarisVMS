using LarisVMS.Vision.Capture;

namespace LarisVMS.Tests;

/// <summary>
/// Covers VisionSession.BuildFfmpegArgs — detection/hardware-acceleration overhaul, pass 1's
/// Letterbox pad-filter insertion, on top of the pre-existing CUDA-vs-software decode split. Pure
/// argument assertions, no ffmpeg process involved, same reasoning as MotionSessionFfmpegArgsTests.
/// </summary>
public class VisionSessionFfmpegArgsTests
{
    private static VisionSessionOptions Options(string? hwaccel = null, LetterboxGeometry? letterbox = null) =>
        new("ffmpeg", "rtsp://camera/sub", 640, 640, hwaccel, letterbox);

    [Fact]
    public void StretchSoftwarePathHasNoPadFilter()
    {
        var args = VisionSession.BuildFfmpegArgs(Options());
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Equal("scale=640:640", vf);
    }

    [Fact]
    public void StretchCudaPathHasNoPadFilter()
    {
        var args = VisionSession.BuildFfmpegArgs(Options("cuda"));
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Equal("scale_cuda=w=640:h=640:format=nv12,hwdownload,format=nv12", vf);
    }

    [Fact]
    public void LetterboxSoftwarePathScalesToTheIntermediateSizeThenPads()
    {
        var letterbox = new LetterboxGeometry(ScaledWidth: 640, ScaledHeight: 360, PadLeft: 0, PadTop: 140);
        var args = VisionSession.BuildFfmpegArgs(Options(letterbox: letterbox));
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Equal("scale=640:360,pad=640:640:0:140:color=black", vf);
    }

    [Fact]
    public void LetterboxCudaPathPadsAfterTheGpuScaleAndHwdownload()
    {
        // The pad filter must come after hwdownload, not before — pad is a plain CPU filter and
        // scale_cuda's own frame is still in GPU memory until hwdownload runs. Getting this order
        // wrong is exactly the class of hwaccel/CPU-filter mismatch that broke privacy-mask burn-in.
        var letterbox = new LetterboxGeometry(ScaledWidth: 360, ScaledHeight: 640, PadLeft: 140, PadTop: 0);
        var args = VisionSession.BuildFfmpegArgs(Options("cuda", letterbox));
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Equal("scale_cuda=w=360:h=640:format=nv12,hwdownload,format=nv12,pad=640:640:140:0:color=black", vf);
    }

    [Fact]
    public void PadTargetIsAlwaysTheFullNetworkSizeRegardlessOfLetterboxAxis()
    {
        var letterbox = new LetterboxGeometry(ScaledWidth: 640, ScaledHeight: 290, PadLeft: 0, PadTop: 175);
        var args = VisionSession.BuildFfmpegArgs(Options(letterbox: letterbox));
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Contains("pad=640:640:0:175:color=black", vf);
    }
}
