using LarisVMS.Vision.Capture;

namespace LarisVMS.Tests;

/// <summary>
/// Covers VisionSession.BuildFfmpegArgs — detection/hardware-acceleration overhaul, pass 1's
/// Letterbox pad-filter insertion, on top of the pre-existing CUDA-vs-software decode split. Pure
/// argument assertions, no ffmpeg process involved, same reasoning as MotionSessionFfmpegArgsTests.
/// </summary>
public class VisionSessionFfmpegArgsTests
{
    private static VisionSessionOptions Options(string? hwaccel = null, LetterboxGeometry? letterbox = null,
        bool nv12 = false, int fpsCap = 0, int width = 640, int height = 640) =>
        new("ffmpeg", "rtsp://camera/sub", width, height, hwaccel, letterbox, nv12, fpsCap);

    [Fact]
    public void DefaultPathEmitsBgra()
    {
        var args = VisionSession.BuildFfmpegArgs(Options("cuda"));
        Assert.Equal("bgra", args[args.ToList().IndexOf("-pix_fmt") + 1]);
    }

    [Fact]
    public void Nv12OutputEmitsNv12WithoutTouchingTheScaleChain()
    {
        var args = VisionSession.BuildFfmpegArgs(Options("cuda", nv12: true));
        Assert.Equal("nv12", args[args.ToList().IndexOf("-pix_fmt") + 1]);
        // The GPU scale/hwdownload chain is unchanged — only the output pixel format differs.
        Assert.Equal("scale_cuda=w=640:h=640:format=nv12,hwdownload,format=nv12",
            args[args.ToList().IndexOf("-vf") + 1]);
    }

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

    [Fact]
    public void FpsCapAppendsAnFpsFilterLastInTheChain()
    {
        var software = VisionSession.BuildFfmpegArgs(Options(fpsCap: 10));
        Assert.Equal("scale=640:640,fps=10", software[software.ToList().IndexOf("-vf") + 1]);

        var letterbox = new LetterboxGeometry(ScaledWidth: 640, ScaledHeight: 360, PadLeft: 0, PadTop: 140);
        var cuda = VisionSession.BuildFfmpegArgs(Options("cuda", letterbox, fpsCap: 12));
        Assert.Equal("scale_cuda=w=640:h=360:format=nv12,hwdownload,format=nv12,pad=640:640:0:140:color=black,fps=12",
            cuda[cuda.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void ZeroFpsCapAddsNoFilter()
    {
        var args = VisionSession.BuildFfmpegArgs(Options(fpsCap: 0));
        Assert.Equal("scale=640:640", args[args.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void HiResCapturePathScalesToTheLargerCaptureSizeWithNoPad()
    {
        // Pass F: the capture buffer is native-aspect at up to 1280 long edge; CameraDetectionPipeline
        // passes Letterbox: null, so ffmpeg does a plain non-square scale and the in-process
        // BgraOps.LetterboxResize handles the pad for inference.
        var software = VisionSession.BuildFfmpegArgs(Options(width: 1280, height: 720));
        Assert.Equal("scale=1280:720", software[software.ToList().IndexOf("-vf") + 1]);

        var cuda = VisionSession.BuildFfmpegArgs(Options("cuda", width: 1280, height: 720, fpsCap: 10));
        Assert.Equal("scale_cuda=w=1280:h=720:format=nv12,hwdownload,format=nv12,fps=10",
            cuda[cuda.ToList().IndexOf("-vf") + 1]);
    }
}
