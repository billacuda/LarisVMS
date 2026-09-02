using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using LarisVMS.Core.Enums;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Capture;

/// <summary>
/// Supervises one ffmpeg process reading a camera's Sub stream as raw BGRA frames for AI object
/// detection, publishing each into a <see cref="LatestFrameSlot"/> for the inference stage to pick
/// up whenever it's ready.
///
/// Deliberately a second, independent RTSP session against Sub — not a tap of the live-view
/// WebSocket, not a third leg on RecordingSession's Main-stream tee. This mirrors
/// LarisVMS.Media.MotionSession's own established shape exactly (opens its own RTSP session
/// against Sub for frame-diff motion) and LarisVMS.Media.SubLiveSession's (opens yet another
/// independent RTSP session against Sub for the adaptive-streaming live tile) — both already treat
/// "duplicate the small process-supervision skeleton rather than touch RecordingSession" as the
/// accepted trade-off in this codebase for exactly this kind of analysis workload. See the object
/// detection plan's decision 1 for why an earlier draft (tapping the live-view WebSocket and
/// decoding fMP4 fragments) was dropped in favor of this shape.
///
/// The frame-reading half — raw fixed-size reads off ffmpeg's stdout, publish-and-drop-stale via
/// LatestFrameSlot, GPU-hybrid decode for CUDA — is ported from aitest's own FrameReader/
/// LatestFrameSlot pipeline (g:\Projects\aitest\src\Aitest.Vision\Capture\FrameReader.cs),
/// verified end-to-end against real cameras there. The process-supervision skeleton around it
/// (Idle -&gt; Connecting -&gt; Recording -&gt; Backoff, the stall watchdog, stderr draining) is
/// MotionSession's, not aitest's — aitest never needed reconnect/backoff plumbing this exact
/// shape since it wasn't written against this codebase's own StreamRecordingState-driven sessions.
/// </summary>
public sealed class VisionSession(VisionSessionOptions options, LatestFrameSlot slot, ILogger logger)
{
    public StreamRecordingState State { get; private set; } = StreamRecordingState.Idle;
    public DateTime? LastFrameAt { get; private set; }
    public string? LastError { get; private set; }

    // nv12 = Y plane (W*H) + interleaved UV (W*H/2); BGRA = 4 bytes/pixel.
    private readonly int _frameSize = options.Nv12Output
        ? checked(options.Width * options.Height * 3 / 2)
        : checked(options.Width * options.Height * 4);

    public async Task RunAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            State = StreamRecordingState.Connecting;
            Process? process = null;

            try
            {
                process = StartFfmpeg();
                var stderrTask = DrainStderrAsync(process, ct);

                LastFrameAt = DateTime.UtcNow; // grace period before the first frame lands
                var readTask = ReadFramesAsync(process, ct);
                var watchdogTask = WatchdogAsync(process, ct);

                await Task.WhenAny(readTask, watchdogTask);
                if (!process.HasExited) TryKill(process);

                await process.WaitForExitAsync(CancellationToken.None);
                await stderrTask;
                try { await readTask; } catch (OperationCanceledException) { }

                if (ct.IsCancellationRequested) break;

                consecutiveFailures++;
                LastError = $"ffmpeg exited with code {process.ExitCode}.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                LastError = ex.Message;
                logger.LogError(ex, "Vision session failed.");
                if (process is { HasExited: false }) TryKill(process);
            }
            finally
            {
                process?.Dispose();
            }

            if (ct.IsCancellationRequested) break;

            State = StreamRecordingState.Backoff;
            var backoffSeconds = Math.Min(60, 2 << Math.Min(consecutiveFailures, 5));
            logger.LogInformation("Vision session backing off {Seconds}s before reconnecting (attempt {Attempt}).", backoffSeconds, consecutiveFailures);
            try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        State = StreamRecordingState.Idle;
    }

    private async Task WatchdogAsync(Process process, CancellationToken ct)
    {
        while (!process.HasExited && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), ct); }
            catch (OperationCanceledException) { return; }

            var staleFor = DateTime.UtcNow - LastFrameAt!.Value;
            if (staleFor.TotalSeconds > options.StalledThresholdSeconds)
            {
                logger.LogWarning("Vision substream stalled ({StaleFor}s since last frame) — restarting ffmpeg.", (int)staleFor.TotalSeconds);
                return;
            }
        }
    }

    private async Task ReadFramesAsync(Process process, CancellationToken ct)
    {
        var stdout = process.StandardOutput.BaseStream;
        var buffer = ArrayPool<byte>.Shared.Rent(_frameSize);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stdout, buffer, _frameSize, ct)) return; // pipe closed — process exiting

                var capturedAt = DateTime.UtcNow;
                LastFrameAt = capturedAt;
                if (State != StreamRecordingState.Recording) State = StreamRecordingState.Recording;

                PublishFrame(buffer, capturedAt);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void PublishFrame(byte[] buffer, DateTime capturedAt)
    {
        slot.Publish(capturedAt, dst => Array.Copy(buffer, dst, _frameSize));
    }

    // Stream.ReadAsync can return a short read even for a live pipe (no guarantee of filling the
    // buffer in one call) — loops until exactly count bytes are read, or the pipe closes (0-byte
    // read) partway through a frame, which is treated as a clean end-of-stream rather than an
    // error: it's the normal shape of ffmpeg exiting mid-frame. Same contract as
    // MotionSession.ReadExactAsync / aitest's FrameReader.ReadExactlyAsync.
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private Process StartFfmpeg()
    {
        var psi = new ProcessStartInfo
        {
            FileName = options.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true, // see RecordingSession.StartFfmpeg's comment on why this is explicit, not inherited
            CreateNoWindow = true
        };

        foreach (var a in BuildFfmpegArgs(options)) psi.ArgumentList.Add(a);

        logger.LogInformation("Starting vision ffmpeg for {RtspUri} at {Width}x{Height} (hwaccel: {Hwaccel}).",
            RedactCredentials(options.RtspUri), options.Width, options.Height, options.HardwareAcceleration ?? "none");

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return process;
    }

    /// <summary>Pure argument construction — internal for direct testing rather than only exercised
    /// through a spawned ffmpeg process, same reasoning as RecordingSession's own
    /// BuildDecodeArgs/BuildCodecArgs/BuildTeeOutputs split and MotionSession.BuildFfmpegArgs.
    ///
    /// GPU-hybrid decode+scale, CUDA only — the only pairing verified end-to-end against real
    /// cameras (in aitest): decode and the first stage of pixel-format conversion happen on the
    /// GPU (scale_cuda -> nv12), and only the already-downscaled frame's final nv12->bgra
    /// conversion finishes on the CPU. scale_cuda's own documented format=bgra option exists in
    /// its filter schema but is not actually implemented by the filter's kernel — requesting it
    /// fails at runtime ("Could not open encoder... Invalid argument"), discovered by testing
    /// against a live stream rather than trusting the documented option list; nv12 is the
    /// confirmed-working output format. Any other -hwaccel value (or none) still requests
    /// hardware decode from ffmpeg where given, but falls back to a plain CPU scale filter —
    /// no other hwaccel+GPU-filter pairing has been verified for this pipeline.
    ///
    /// Pass 1 of the detection/hardware-acceleration overhaul: when options.Letterbox is set, a
    /// `pad=` filter step is inserted after the aspect-preserving scale — plain, always-CPU, and
    /// deliberately NOT attempted as part of scale_cuda's own GPU filter chain the way the scale
    /// itself is. scale_cuda's own option list has already been proven partly aspirational once
    /// (the format=bgra case above) — a hand-rolled GPU letterbox pad is exactly the kind of
    /// unverified pairing that bit privacy-mask burn-in (hwaccel decode + a CPU-only filter broke
    /// outright, v0.112.0). Padding is a fixed, small border write, not a per-pixel resize, so
    /// paying it on the CPU costs far less than the risk of an unverified GPU pairing.</summary>
    internal static IReadOnlyList<string> BuildFfmpegArgs(VisionSessionOptions options)
    {
        var useGpuScale = string.Equals(options.HardwareAcceleration, "cuda", StringComparison.OrdinalIgnoreCase);

        var args = new List<string> { "-nostdin", "-rtsp_transport", "tcp", "-timeout", "5000000" };

        if (!string.IsNullOrWhiteSpace(options.HardwareAcceleration))
        {
            args.Add("-hwaccel"); args.Add(options.HardwareAcceleration);
            if (useGpuScale)
            {
                // Keeps the decoded frame in GPU memory instead of ffmpeg's default of downloading
                // it immediately after decode — required for scale_cuda below to actually run on
                // the GPU rather than receiving a frame it can't process.
                args.Add("-hwaccel_output_format"); args.Add("cuda");
            }
        }

        args.Add("-i"); args.Add(options.RtspUri);
        args.Add("-an");

        var scaleWidth = options.Letterbox?.ScaledWidth ?? options.Width;
        var scaleHeight = options.Letterbox?.ScaledHeight ?? options.Height;
        var padFilter = options.Letterbox is { } lb
            ? string.Create(CultureInfo.InvariantCulture,
                $",pad={options.Width}:{options.Height}:{lb.PadLeft}:{lb.PadTop}:color=black")
            : "";
        // Frame-rate cap last in the chain (on CPU frames — post-hwdownload for the GPU path) so it
        // only ever runs on frames that survived. Node guarantees FpsCap is below the stream's real
        // rate, so this only drops, never duplicates.
        var fpsFilter = options.FpsCap > 0
            ? string.Create(CultureInfo.InvariantCulture, $",fps={options.FpsCap}")
            : "";

        if (useGpuScale)
        {
            args.Add("-vf"); args.Add(string.Create(CultureInfo.InvariantCulture,
                $"scale_cuda=w={scaleWidth}:h={scaleHeight}:format=nv12,hwdownload,format=nv12{padFilter}{fpsFilter}"));
        }
        else
        {
            args.Add("-vf"); args.Add(string.Create(CultureInfo.InvariantCulture,
                $"scale={scaleWidth}:{scaleHeight}{padFilter}{fpsFilter}"));
        }

        // Emit each decoded frame exactly once. Without this, ffmpeg converts the output to a
        // constant rate taken from the stream's r_frame_rate — its guess at the least common
        // multiple of observed frame durations, not the real rate. Confirmed live against a real
        // camera reporting r_frame_rate=100 against an actual 20fps: every frame was duplicated
        // five times without this flag.
        args.Add("-fps_mode"); args.Add("passthrough");

        // Pass 4a: nv12 straight through when the engine has the GPU preprocessing head — no
        // swscale colour conversion on the CPU at all. Otherwise BGRA, as before.
        args.Add("-pix_fmt"); args.Add(options.Nv12Output ? "nv12" : "bgra");
        args.Add("-f"); args.Add("rawvideo");
        args.Add("-");

        return args;
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
            {
                logger.LogDebug("ffmpeg (vision): {Line}", line);

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("could not", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("ffmpeg (vision): {Line}", line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogDebug(ex, "Vision stderr drain ended."); }
    }

    private void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to kill vision ffmpeg process (may have already exited)."); }
    }

    private static string RedactCredentials(string rtspUri)
    {
        try
        {
            var uri = new Uri(rtspUri);
            if (string.IsNullOrEmpty(uri.UserInfo)) return rtspUri;
            return rtspUri.Replace(uri.UserInfo, "***");
        }
        catch { return rtspUri; }
    }
}
