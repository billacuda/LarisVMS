using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Serializes detection-engine construction across every camera pipeline in this process, and drops
/// the process priority while a build is in flight.
///
/// Both halves exist for the same incident. Building an engine is a long synchronous native call —
/// with TensorRT enabled, the first build for a given model compiles an engine, which takes minutes
/// and saturates every core. Camera pipelines used to construct their engines concurrently (Node
/// fires every /start at once), so a six-camera node ran six TensorRT builders simultaneously on a
/// machine already running one recording ffmpeg, one motion ffmpeg and one vision ffmpeg per camera.
/// The recorder's own tee muxer writes its live leg to a pipe Node drains in-process; starve those
/// drain tasks of CPU and the pipe fills, the tee blocks, and *both* the live leg and the segment-file
/// leg stop — silently, since ffmpeg is blocked on a write rather than failing. That presented as
/// "turning TensorRT on stops recording and live view", recovering on its own once the builds
/// finished.
///
/// Serializing also fixes a second problem the concurrent path had: every build shares one
/// <c>trt_timing_cache_path</c> file (see <see cref="OrtSessionFactory"/>), and several builders
/// writing it at once is not a supported arrangement. Serialized, the first camera pays the build
/// and the rest load from the engine cache, which is the behavior the cache was added for.
///
/// This is the same cross-camera, process-wide gate reasoning as CameraPipelineManager's own
/// high-res re-detection gate — a limit on how much of one machine's GPU/CPU the detection tier may
/// take at once, which only means anything if it spans cameras.
/// </summary>
public static class EngineBuildGate
{
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Runs <paramref name="build"/> with at most one build in flight process-wide. Synchronous and
    /// blocking by design — callers hand this to <c>Task.Run</c> so no request thread is the one
    /// waiting. <paramref name="options"/> is used only for logging (which model, whether the
    /// TensorRT cache was already warm).
    ///
    /// The queue wait honors <paramref name="ct"/>, so a camera stopped while queued behind another
    /// camera's multi-minute build unwinds immediately instead of holding its pipeline's disposal
    /// open. The build itself is a native call and cannot be cancelled once started.
    /// </summary>
    public static IDetectionEngine Build(Guid cameraId, EngineOptions options,
        Func<IDetectionEngine> build, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(logger);

        // Said before the wait, not after: a camera queued behind another camera's cold TensorRT
        // compile is otherwise completely silent for however long that takes, which reads from the
        // log exactly like a camera that failed to start.
        if (_gate.CurrentCount == 0)
        {
            logger.LogInformation(
                "Camera {CameraId} is waiting for another camera's detection engine build to finish before starting its own.",
                cameraId);
        }

        var waited = Stopwatch.StartNew();
        _gate.Wait(ct);
        waited.Stop();

        // Only meaningful for a TensorRT build; a cache that already holds a .engine for this model
        // makes the difference between a sub-second load and a multi-minute compile, and an operator
        // reading the log during a cold start has no other way to tell which one they're waiting on.
        var cacheWarm = options.EnableTensorRt && TensorRtCacheHasEntries(options.TensorRtEngineCachePath);
        if (options.EnableTensorRt)
        {
            // Two messages rather than one with a substituted word: the cold case needs to say how
            // long this will take (minutes, and nothing else starts meanwhile), the warm case needs
            // to say the opposite, and a single sentence covering both ends up asserting a cache miss
            // on a line that has just reported the cache is warm.
            if (cacheWarm)
            {
                logger.LogInformation(
                    "Loading the detection engine for camera {CameraId} from {ModelPath} — the TensorRT engine cache is " +
                    "warm, so this is a load rather than a compile. Waited {WaitedMs} ms for the process-wide build gate.",
                    cameraId, options.ModelPath, waited.ElapsedMilliseconds);
            }
            else
            {
                logger.LogInformation(
                    "Building the detection engine for camera {CameraId} from {ModelPath} — the TensorRT engine cache is " +
                    "cold, so this compiles the engine, which takes minutes and happens once per model. Every other " +
                    "camera's engine waits behind this one. Waited {WaitedMs} ms for the process-wide build gate.",
                    cameraId, options.ModelPath, waited.ElapsedMilliseconds);
            }
        }

        // Below Normal for the duration of the build, restored after. The priority *class* is what
        // reaches TensorRT's own native builder threads — nothing this process schedules itself — and
        // those are what actually starve the node's recording drain loops. Safe to set and restore
        // unconditionally here because the gate guarantees only one build is ever in flight.
        var process = Process.GetCurrentProcess();
        var originalPriority = TryGetPriority(process, logger);
        if (originalPriority is not null) TrySetPriority(process, ProcessPriorityClass.BelowNormal, logger);

        var elapsed = Stopwatch.StartNew();
        try
        {
            return build();
        }
        finally
        {
            elapsed.Stop();
            if (originalPriority is { } restore) TrySetPriority(process, restore, logger);

            if (options.EnableTensorRt)
            {
                logger.LogInformation(
                    "Detection engine build for camera {CameraId} finished in {ElapsedMs} ms (cache was {CacheState}).",
                    cameraId, elapsed.ElapsedMilliseconds, cacheWarm ? "warm" : "cold");
            }

            _gate.Release();
        }
    }

    private static bool TensorRtCacheHasEntries(string? cachePath)
    {
        if (string.IsNullOrWhiteSpace(cachePath)) return false;
        try { return Directory.Exists(cachePath) && Directory.EnumerateFiles(cachePath, "*.engine").Any(); }
        catch { return false; } // a log-only signal; never worth failing a build over
    }

    // Priority changes are pure optimization: a host that refuses them (a constrained job object, a
    // policy) must still get its engine built, so both directions are best-effort and log at Debug.
    private static ProcessPriorityClass? TryGetPriority(Process process, ILogger logger)
    {
        try { return process.PriorityClass; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read this process's priority class — the engine build will run at the default priority.");
            return null;
        }
    }

    private static void TrySetPriority(Process process, ProcessPriorityClass priority, ILogger logger)
    {
        try { process.PriorityClass = priority; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not set this process's priority class to {Priority}.", priority);
        }
    }
}
