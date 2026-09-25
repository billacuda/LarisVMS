using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Service;

/// <summary>
/// Ends this process when local inference has died process-wide, so Node's VisionServiceSupervisor
/// (which only notices a child that has exited) restarts it and NodeWorker's reconcile re-issues every
/// camera's start. Confirmed need: a GPU driver reset (TDR) on nvr1 2026-09-24 05:52 left the process's
/// CUDA/TensorRT context permanently broken — every camera failed every frame for ~12 hours while the
/// process stayed up, until a manual node restart. That context can't be repaired in-process, so a
/// fresh process is the recovery.
///
/// Fires only when at least one camera is <see cref="InferenceHealth.Stalled"/> and none is
/// <see cref="InferenceHealth.Healthy"/>: one camera with a broken engine of its own while the others
/// work is not something a restart fixes (its cadence line reports the failures), and an offline
/// camera has no frames to be stalled on.
/// </summary>
public sealed class InferenceWatchdog(CameraPipelineManager manager, ILogger<InferenceWatchdog> logger) : BackgroundService
{
    internal static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var health = manager.GetInferenceHealth(DateTime.UtcNow, StallAfter);
                if (!ShouldRestart(health.Select(h => h.Health))) continue;

                logger.LogError(
                    "Inference has produced no result for {Seconds:F0}s on any camera while frames kept arriving " +
                    "(stalled: {Cameras}) — most likely the GPU was reset under this process (check the System event " +
                    "log for nvlddmkm / Display 4101). Exiting so the node restarts LarisVMS.Vision.Service.",
                    StallAfter.TotalSeconds,
                    string.Join(", ", health.Where(h => h.Health == InferenceHealth.Stalled).Select(h => h.Camera)));

                // Kill rather than a graceful host shutdown: shutdown disposes every pipeline, and a
                // pipeline whose native inference call is hung on the dead GPU never finishes disposing.
                // The file logger auto-flushes, so the line above is already on disk; this process's own
                // ffmpeg children exit on the broken stdout pipe.
                Process.GetCurrentProcess().Kill();
            }
        }
        catch (OperationCanceledException) { }
    }

    internal static bool ShouldRestart(IEnumerable<InferenceHealth> cameras)
    {
        var anyStalled = false;
        foreach (var health in cameras)
        {
            if (health == InferenceHealth.Healthy) return false;
            if (health == InferenceHealth.Stalled) anyStalled = true;
        }
        return anyStalled;
    }
}
