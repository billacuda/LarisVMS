using LarisVMS.Vision.Service;

namespace LarisVMS.Tests;

/// <summary>
/// Covers CameraDetectionPipeline's pure helpers. The pipeline itself needs an ffmpeg process and an
/// ONNX Runtime session, so only the standalone geometry math is unit-tested here.
/// </summary>
public class CameraDetectionPipelineTests
{
    [Fact]
    public void CaptureDimensionsAreTheSourceItselfWhenItIsAtOrBelowTheCap()
    {
        // 720p Sub stream, 640 network — capture at native res, the ~2x sharpness win.
        Assert.Equal((1280, 720), CameraDetectionPipeline.ComputeCaptureDimensions(1280, 720, 640));
        // A small Sub stream — no upscaling past the source (Pass F is a no-op here, by design).
        Assert.Equal((640, 480), CameraDetectionPipeline.ComputeCaptureDimensions(640, 480, 640));
    }

    [Fact]
    public void CaptureLongEdgeIsCappedAt1280PreservingAspect()
    {
        // 1080p Sub stream -> long edge clamped to 1280, aspect kept (16:9 -> 1280x720).
        Assert.Equal((1280, 720), CameraDetectionPipeline.ComputeCaptureDimensions(1920, 1080, 640));
        // Portrait: the *long* edge is the one that gets capped.
        Assert.Equal((720, 1280), CameraDetectionPipeline.ComputeCaptureDimensions(1080, 1920, 640));
    }

    [Fact]
    public void CaptureIsNeverBelowTheNetworkSize()
    {
        // A Sub stream smaller than the network input still decodes at least at the network size —
        // never below it, so the inference downscale is only ever a downscale.
        var (w, h) = CameraDetectionPipeline.ComputeCaptureDimensions(320, 240, 640);
        Assert.True(Math.Max(w, h) >= 640);
    }

    [Fact]
    public void CaptureDimensionsAreAlwaysEven()
    {
        // An odd source that a cap-scale would otherwise round to an odd intermediate size.
        var (w, h) = CameraDetectionPipeline.ComputeCaptureDimensions(1999, 1101, 640);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.True(Math.Max(w, h) <= 1280);
    }
}
