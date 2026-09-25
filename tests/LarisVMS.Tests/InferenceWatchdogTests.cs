using LarisVMS.Vision.Service;

namespace LarisVMS.Tests;

/// <summary>
/// Locks in the recovery for a confirmed outage (nvr1, 2026-09-24 05:52→17:39): a GPU driver reset left
/// every camera's TensorRT session failing every frame with the vision process still alive, so nothing
/// restarted it. See <see cref="InferenceWatchdog"/> and <see cref="CameraDetectionPipeline.GetInferenceHealth"/>.
/// </summary>
public class InferenceWatchdogTests
{
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(60);

    [Fact]
    public void RecentSuccessIsHealthy() =>
        Assert.Equal(InferenceHealth.Healthy,
            CameraDetectionPipeline.ClassifyInferenceHealth(true, TimeSpan.FromSeconds(5), 500, StallAfter));

    [Fact]
    public void NoSuccessWhileFramesKeepArrivingIsStalled() =>
        Assert.Equal(InferenceHealth.Stalled,
            CameraDetectionPipeline.ClassifyInferenceHealth(true, TimeSpan.FromSeconds(90), 600, StallAfter));

    [Fact]
    public void NoSuccessWithNoFramesIsNotStalled() =>
        // An offline camera has nothing to infer on — restarting the process would not help it.
        Assert.Equal(InferenceHealth.NotApplicable,
            CameraDetectionPipeline.ClassifyInferenceHealth(true, TimeSpan.FromMinutes(10),
                CameraDetectionPipeline.MinFramesForStall - 1, StallAfter));

    [Fact]
    public void NoLocalEngineIsNeverJudged() =>
        // Still building, build failed, or the external HTTP backend.
        Assert.Equal(InferenceHealth.NotApplicable,
            CameraDetectionPipeline.ClassifyInferenceHealth(false, TimeSpan.FromMinutes(10), 600, StallAfter));

    [Fact]
    public void RestartsWhenEveryJudgedCameraIsStalled() =>
        Assert.True(InferenceWatchdog.ShouldRestart(
            [InferenceHealth.Stalled, InferenceHealth.Stalled, InferenceHealth.NotApplicable]));

    [Fact]
    public void OneHealthyCameraPreventsRestart() =>
        // A single camera's own broken engine is not something a process restart fixes.
        Assert.False(InferenceWatchdog.ShouldRestart([InferenceHealth.Stalled, InferenceHealth.Healthy]));

    [Fact]
    public void NothingStalledNeverRestarts()
    {
        Assert.False(InferenceWatchdog.ShouldRestart([]));
        Assert.False(InferenceWatchdog.ShouldRestart([InferenceHealth.NotApplicable, InferenceHealth.NotApplicable]));
    }
}
