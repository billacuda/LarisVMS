using LarisVMS.Media;
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
        Assert.Equal("scale_cuda=w=640:h=640:format=nv12,hwdownload,format=nv12,showinfo",
            args[args.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void StretchSoftwarePathHasNoPadFilter()
    {
        var args = VisionSession.BuildFfmpegArgs(Options());
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Equal("scale=640:640,showinfo", vf);
    }

    [Fact]
    public void StretchCudaPathHasNoPadFilter()
    {
        var args = VisionSession.BuildFfmpegArgs(Options("cuda"));
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Equal("scale_cuda=w=640:h=640:format=nv12,hwdownload,format=nv12,showinfo", vf);
    }

    [Fact]
    public void LetterboxSoftwarePathScalesToTheIntermediateSizeThenPads()
    {
        var letterbox = new LetterboxGeometry(ScaledWidth: 640, ScaledHeight: 360, PadLeft: 0, PadTop: 140);
        var args = VisionSession.BuildFfmpegArgs(Options(letterbox: letterbox));
        var vf = args[args.ToList().IndexOf("-vf") + 1];

        Assert.Equal("scale=640:360,pad=640:640:0:140:color=black,showinfo", vf);
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

        Assert.Equal("scale_cuda=w=360:h=640:format=nv12,hwdownload,format=nv12,pad=640:640:140:0:color=black,showinfo", vf);
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
    public void FpsCapAppendsASelectFilterLastInTheChain()
    {
        var software = VisionSession.BuildFfmpegArgs(Options(fpsCap: 10));
        Assert.Equal("scale=640:640,select='isnan(prev_selected_t)+gte(floor(t*10),floor(prev_selected_t*10)+1)',showinfo", software[software.ToList().IndexOf("-vf") + 1]);

        var letterbox = new LetterboxGeometry(ScaledWidth: 640, ScaledHeight: 360, PadLeft: 0, PadTop: 140);
        var cuda = VisionSession.BuildFfmpegArgs(Options("cuda", letterbox, fpsCap: 12));
        Assert.Equal("scale_cuda=w=640:h=360:format=nv12,hwdownload,format=nv12,pad=640:640:0:140:color=black,select='isnan(prev_selected_t)+gte(floor(t*12),floor(prev_selected_t*12)+1)',showinfo",
            cuda[cuda.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void ZeroFpsCapAddsNoFilter()
    {
        var args = VisionSession.BuildFfmpegArgs(Options(fpsCap: 0));
        Assert.Equal("scale=640:640,showinfo", args[args.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void HiResCapturePathScalesToTheLargerCaptureSizeWithNoPad()
    {
        // Pass F: the capture buffer is native-aspect at up to 1280 long edge; CameraDetectionPipeline
        // passes Letterbox: null, so ffmpeg does a plain non-square scale and the in-process
        // BgraOps.LetterboxResize handles the pad for inference.
        var software = VisionSession.BuildFfmpegArgs(Options(width: 1280, height: 720));
        Assert.Equal("scale=1280:720,showinfo", software[software.ToList().IndexOf("-vf") + 1]);

        var cuda = VisionSession.BuildFfmpegArgs(Options("cuda", width: 1280, height: 720, fpsCap: 10));
        Assert.Equal("scale_cuda=w=1280:h=720:format=nv12,hwdownload,format=nv12,select='isnan(prev_selected_t)+gte(floor(t*10),floor(prev_selected_t*10)+1)',showinfo",
            cuda[cuda.ToList().IndexOf("-vf") + 1]);
    }

    [Fact]
    public void CorridorCameraCorrectedToPortraitPadsLeftAndRight()
    {
        // End of the chain AiDetection.Orientation drives: a 480x704 source through
        // InferenceProfile gives ScaledWidth 436 / PadLeft 102 (see InferenceProfileTests), which has
        // to reach ffmpeg as a left/right pad. Pinned against the real command line observed on the
        // recorder, since this is the difference between an undistorted frame and a squashed one.
        var letterbox = new LetterboxGeometry(ScaledWidth: 436, ScaledHeight: 640, PadLeft: 102, PadTop: 0);
        var args = VisionSession.BuildFfmpegArgs(Options("cuda", letterbox));

        Assert.Equal("scale_cuda=w=436:h=640:format=nv12,hwdownload,format=nv12,pad=640:640:102:0:color=black,showinfo",
            args[args.ToList().IndexOf("-vf") + 1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cuda")]
    [InlineData("d3d11va")]
    public void DecodesWithLowLatencyOptionsBeforeTheInput(string? hwaccel)
    {
        // Frame-threaded decode delayed every frame (and so every live-view box) by ~600 ms.
        var args = VisionSession.BuildFfmpegArgs(Options(hwaccel)).ToList();
        var input = args.IndexOf("-i");

        Assert.Equal("slice", args[args.IndexOf("-thread_type") + 1]);
        Assert.Equal("low_delay", args[args.IndexOf("-flags") + 1]);
        Assert.Equal("nobuffer", args[args.IndexOf("-fflags") + 1]);
        Assert.True(args.IndexOf("-thread_type") < input, "decoder options must come before -i");
        Assert.True(args.IndexOf("-flags") < input, "decoder options must come before -i");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cuda")]
    public void StampsPacketsWithArrivalTimeAndPrintsThemLastInTheChain(string? hwaccel)
    {
        // Frames are stamped by RTSP arrival (FrameArrivalStamps), so the wallclock option has to be
        // an input option and showinfo has to see exactly the frames that reach stdout.
        var args = VisionSession.BuildFfmpegArgs(Options(hwaccel, fpsCap: 7)).ToList();

        Assert.Equal("1", args[args.IndexOf("-use_wallclock_as_timestamps") + 1]);
        Assert.True(args.IndexOf("-use_wallclock_as_timestamps") < args.IndexOf("-i"), "must be an input option");
        // Without it ffmpeg rebases the wall-clock pts to start at 0.
        Assert.Contains("-copyts", args);
        Assert.EndsWith(",showinfo", args[args.IndexOf("-vf") + 1]);
    }
}

public class FrameArrivalStampsTests
{
    private const string TimeBase = "[Parsed_showinfo_4 @ 000001d2c3a4b5c0] config in time_base: 1/90000, frame_rate: 25/1";

    // 2026-10-04 16:00:00.250 UTC in 1/90000 units.
    private static readonly DateTime Arrival = new(2026, 10, 4, 16, 0, 0, 250, DateTimeKind.Utc);
    private static readonly long ArrivalPts = (long)((Int128)(Arrival - DateTime.UnixEpoch).Ticks * 90000 / TimeSpan.TicksPerSecond);

    private static string FrameLine(long n, long pts) =>
        $"[Parsed_showinfo_4 @ 000001d2c3a4b5c0] n:{n,4} pts:{pts,7} pts_time:1.7596e+09 duration:   3600 fmt:bgra cl:left sar:1/1 s:640x640 i:P iskey:1 type:I";

    [Fact]
    public async Task ParsesTheArrivalTimeFromPtsAndTimeBase()
    {
        var stamps = new FrameArrivalStamps();
        Assert.True(stamps.TryParseLine(TimeBase));
        Assert.True(stamps.TryParseLine(FrameLine(0, ArrivalPts)));

        var stamp = await stamps.TakeAsync(0, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.NotNull(stamp);
        Assert.InRange((stamp.Value - Arrival).Duration().TotalMilliseconds, 0, 1);
    }

    [Fact]
    public void IgnoresOtherFfmpegOutput()
    {
        var stamps = new FrameArrivalStamps();
        Assert.False(stamps.TryParseLine("Stream #0:0: Video: hevc (Main), yuvj420p(pc), 2560x1440, 20 fps"));
        Assert.False(stamps.TryParseLine("frame=  120 fps=7.0 q=-0.0 size=N/A time=00:00:17.14"));
    }

    [Fact]
    public async Task ReturnsNullImmediatelyWhenFfmpegNeverPrintsShowinfo()
    {
        var stamps = new FrameArrivalStamps();
        var started = DateTime.UtcNow;

        Assert.Null(await stamps.TakeAsync(0, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "must not wait for a stamp that will never come");
    }

    [Fact]
    public async Task AMissingLineCostsOnlyItsOwnFrame()
    {
        var stamps = new FrameArrivalStamps();
        stamps.TryParseLine(TimeBase);
        stamps.TryParseLine(FrameLine(0, ArrivalPts));
        // Frame 1's line never arrives; frame 2's does.
        stamps.TryParseLine(FrameLine(2, ArrivalPts + 9000));

        var wait = TimeSpan.FromMilliseconds(20);
        Assert.NotNull(await stamps.TakeAsync(0, wait, CancellationToken.None));
        Assert.Null(await stamps.TakeAsync(1, wait, CancellationToken.None));
        var third = await stamps.TakeAsync(2, wait, CancellationToken.None);

        Assert.NotNull(third);
        Assert.InRange((third.Value - Arrival.AddMilliseconds(100)).Duration().TotalMilliseconds, 0, 1);
    }
}
