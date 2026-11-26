using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Media;

/// <summary>
/// Waits for a killed ffmpeg and its pipe readers, but not forever. A process stuck in uninterruptible
/// I/O (a network share that went away, a GPU driver) can outlive its kill by hours; a session waiting
/// for it without a limit is frozen for as long (see RecordingSessionOptions.KillTimeoutSeconds).
/// </summary>
public static class ProcessExit
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static string AbandonedMessage { get; } =
        $"ffmpeg did not exit within {DefaultTimeout.TotalSeconds:0}s of being killed — abandoned and restarting.";

    /// <summary>True when the process exited and its readers finished in time. False when it was
    /// abandoned: it's left to exit by itself, and <paramref name="readers"/> must not be awaited.
    /// Never throws for a reader that failed; await the reader itself to see that.</summary>
    public static async Task<bool> WaitAsync(Process process, Task readers, ILogger logger, TimeSpan? timeout = null)
    {
        var all = Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), readers);
        if (await Task.WhenAny(all, Task.Delay(timeout ?? DefaultTimeout)) == all) return true;

        logger.LogWarning("ffmpeg did not exit within {Timeout}s of being killed — abandoning this attempt and starting a fresh one.",
            (int)(timeout ?? DefaultTimeout).TotalSeconds);
        // Observed, so however it ends is never an unobserved exception.
        _ = all.ContinueWith(t => logger.LogDebug(t.Exception, "The abandoned ffmpeg attempt finished."),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return false;
    }
}
