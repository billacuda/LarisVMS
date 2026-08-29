using System.Diagnostics;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Enums;

namespace LarisVMS.Media;

/// <summary>One ServerMotion zone's precomputed pixel mask (Ignore zones already subtracted — see
/// ZoneRasterizer) and the sensitivity threshold MotionHysteresis compares each frame's score
/// against.</summary>
public record MotionZoneMask(Guid ZoneId, bool[] Mask, double Sensitivity);

public record MotionSessionOptions(
    string FfmpegPath,
    string RtspUri,
    /// <summary>Fixed working resolution for the whole pipeline, not the camera's actual substream
    /// resolution — ffmpeg's own -vf scale does the resizing. Fixed (not "preserve aspect ratio")
    /// specifically so every frame is exactly Width*Height bytes and can be read from stdout in
    /// constant-size chunks with no stream/box parsing needed, unlike RecordingSession's segment
    /// files. Motion sensitivity doesn't need correct aspect ratio, only frame-to-frame consistency.</summary>
    int Width = 320,
    int Height = 240,
    int Fps = 5,
    /// <summary>Per-pixel grayscale delta (0-255) that counts as "changed" — see MotionDetector.Score.</summary>
    byte PixelDeltaThreshold = 25,
    int StalledThresholdSeconds = 30,
    /// <summary>ffmpeg -hwaccel value, e.g. "cuda" — pass 0 of the detection/hardware-acceleration
    /// overhaul. Before this, every ServerMotion camera ran a continuous *software* video decode of
    /// its Sub stream regardless of the node's resolved accelerator — the single largest CPU cost in
    /// the whole detection stack, measured directly against VisionSession's own already-GPU-decoded
    /// pipeline and RecordingSession's/SubLiveSession's `-c copy` (near-zero). Only "cuda" changes
    /// anything here, same restriction as VisionSession.StartFfmpeg's own doc comment: scale_cuda's
    /// GPU-hybrid decode+scale is the only hwaccel/filter pairing verified end to end in this
    /// codebase. Any other value (or null) keeps today's plain software `scale=W:H,format=gray` path
    /// unchanged — Intel/AMD come later.</summary>
    string? HardwareAcceleration = null)
{
    public TimeSpan StartAfter { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan EndAfter { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Supervises one ffmpeg process reading a camera's substream as a fixed-size rawvideo pipe (plain
/// software-decoded grayscale, or GPU-decoded nv12 — see MotionSessionOptions.HardwareAcceleration
/// and _yPlaneSize's own comment for why both feed MotionDetector.Score identically), diffing
/// consecutive frames per zone (MotionDetector), and turning threshold crossings into discrete spans
/// (MotionHysteresis) — one hysteresis instance per zone, since each zone's motion
/// state is independent. Deliberately not tee'd with recording or live view: it opens its own RTSP
/// session against the Sub stream, the first real consumer of Sub in this codebase (Main is
/// recording-only; Sub was reserved for the live wall and motion by the plan, but M5 never actually
/// implemented live's auto-switch-to-Sub path, so this is genuinely new ground — flagged as
/// unverified against a real multi-session camera until confirmed).
///
/// Same Idle -&gt; Connecting -&gt; Recording -&gt; Backoff shape as RecordingSession, simplified: no
/// segment files, no live fanout, no stderr resolution parsing (the fixed-size Width*Height output
/// makes that unnecessary — see MotionSessionOptions.Width/Height).
/// </summary>
public sealed class MotionSession(MotionSessionOptions options, IReadOnlyList<MotionZoneMask> zones, ILogger logger)
{
    public StreamRecordingState State { get; private set; } = StreamRecordingState.Idle;
    public DateTime? LastFrameAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised once per completed span, per zone. NodeWorker attaches CameraId when
    /// enqueuing for report — this class stays camera-agnostic, same as RecordingSession.</summary>
    public event Action<Guid /*zoneId*/, MotionSpanResult>? MotionSpanCompleted;

    // The Y (luma) plane is always Width*Height bytes and is grayscale by construction — both the
    // plain software path (format=gray, one byte/pixel already) and the CUDA path (nv12's Y plane,
    // read as-is) hand MotionDetector.Score exactly the same shape of data, so no format-specific
    // branch exists anywhere below this line. Whether the CUDA path is active only affects how many
    // *extra* bytes ffmpeg puts on the wire per frame (nv12's interleaved UV chroma plane, half the Y
    // plane's size for 4:2:0) — see ComputeCaptureFrameSize.
    private readonly int _yPlaneSize = options.Width * options.Height;
    private readonly int _frameSize = ComputeCaptureFrameSize(options);

    /// <summary>Only "cuda" gets the GPU-hybrid decode path — see MotionSessionOptions
    /// .HardwareAcceleration's own doc comment for why every other value stays on plain software
    /// decode for now.</summary>
    internal static bool UsesNv12(string? hardwareAcceleration) =>
        string.Equals(hardwareAcceleration, "cuda", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bytes ffmpeg actually puts on the pipe per frame: just the Y plane for today's
    /// software `format=gray` path, or a full nv12 frame (Y plane + half-size interleaved UV plane,
    /// 4:2:0 chroma subsampling) for the CUDA path — nv12 is a whole-frame format, so the chroma
    /// bytes arrive whether this class wants them or not; they're read and immediately discarded by
    /// ReadFramesAsync rather than fed to MotionDetector.Score. internal for direct testing, same
    /// reasoning as RecordingSession.BuildTeeOutputs/BuildCodecArgs's own pure-method extraction.</summary>
    internal static int ComputeCaptureFrameSize(MotionSessionOptions options)
    {
        var yPlaneSize = options.Width * options.Height;
        return UsesNv12(options.HardwareAcceleration) ? yPlaneSize + yPlaneSize / 2 : yPlaneSize;
    }

    // A field (built once, for the instance's whole lifetime) rather than a RunAsync-local, so
    // HasMotionSince/GetInProgressSpans can be queried from outside RunAsync's own async context at
    // any time — including before the first frame ever arrives (every zone starts with no motion
    // observed, which is the correct answer for a session that hasn't seen anything yet).
    private readonly Dictionary<Guid, MotionHysteresis> _hysteresis =
        zones.ToDictionary(z => z.ZoneId, _ => new MotionHysteresis(options.StartAfter, options.EndAfter));

    /// <summary>M8 pass 3: true if any zone on this camera has ever observed motion at or after
    /// <paramref name="thresholdUtc"/>, as of right now. Any zone counts, not just one — a segment
    /// stays if *any* watched area had activity.
    ///
    /// This is the query that makes pre-roll, post-roll, *and* motion during the segment itself all
    /// fall out of one comparison, exactly because LastMotionAtUtc only ever moves forward (see its
    /// doc comment). NodeWorker defers a segment's keep/discard decision until PreRoll seconds after
    /// that segment ends, then asks HasMotionSince(segment.StartUtc - PostRoll) at that later moment:
    /// if motion happened anywhere from PostRoll *before the segment started* through PreRoll *after
    /// it ended*, this is true — covering "we're in the post-roll tail of an earlier event," "this
    /// segment turned out to be the pre-roll for an event that hadn't started yet when it completed,"
    /// AND "the motion happened somewhere in the middle of the segment itself" with the same call.
    /// Anchoring to StartUtc (not EndUtc, an earlier version of this) matters: anchoring to EndUtc
    /// silently discarded segments with real, detected motion whenever that motion happened more than
    /// PostRoll seconds before the segment's own end — invisible with the original 30s default (most
    /// of a 60s segment falls within 30s of its end) but a real, confirmed bug once an operator set a
    /// smaller PostRoll.</summary>
    public bool HasMotionSince(DateTime thresholdUtc)
        => _hysteresis.Values.Any(h => h.LastMotionAtUtc is { } t && t >= thresholdUtc);

    /// <summary>M8: every zone with recent activity (confirmed span or not — see MotionHysteresis
    /// .CurrentInProgressSpan's doc comment for why confirmation isn't required here), as a
    /// checkpoint snapshot ending at <paramref name="nowUtc"/> — what NodeWorker reports
    /// periodically so both a long-running span (a genuinely active scene) and frequent short
    /// unconfirmed bursts (which already drive the keep/discard decision via LastMotionAtUtc, but
    /// otherwise never produce a reportable span) show up in MotionSpans instead of being invisible.
    /// <paramref name="recency"/> should be a little larger than the caller's own checkpoint
    /// interval, so activity from just before the previous tick isn't missed on a borderline
    /// timing.</summary>
    public IEnumerable<(Guid ZoneId, MotionSpanResult Span)> GetInProgressSpans(DateTime nowUtc, TimeSpan recency)
    {
        foreach (var (zoneId, h) in _hysteresis)
        {
            if (h.CurrentInProgressSpan(nowUtc, recency) is { } span) yield return (zoneId, span);
        }
    }

    /// <summary>Test-only seam: drives a specific zone's hysteresis directly (simulating frames)
    /// without spawning ffmpeg via RunAsync. Mirrors RecordingSession.TryParseVideoStreamLine's
    /// internal-for-testability pattern.</summary>
    internal MotionHysteresis? TryGetZoneHysteresis(Guid zoneId) => _hysteresis.GetValueOrDefault(zoneId);

    public async Task RunAsync(CancellationToken ct)
    {
        var hysteresis = _hysteresis;
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
                var readTask = ReadFramesAsync(process, hysteresis, ct);
                var watchdogTask = WatchdogAsync(process, ct);

                await Task.WhenAny(readTask, watchdogTask);
                if (!process.HasExited) TryKill(process);

                await process.WaitForExitAsync(CancellationToken.None);
                await stderrTask;
                try { await readTask; } catch (OperationCanceledException) { }

                // A reconnect means the next frame has no valid predecessor — flush every zone's
                // in-progress span now rather than let the coming frame gap read as a giant, bogus
                // diff against a frame from a different RTSP session.
                FlushAll(hysteresis, DateTime.UtcNow);

                if (ct.IsCancellationRequested) break;

                consecutiveFailures++;
                LastError = $"ffmpeg exited with code {process.ExitCode}.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                LastError = ex.Message;
                logger.LogError(ex, "Motion session failed.");
                if (process is { HasExited: false }) TryKill(process);
                FlushAll(hysteresis, DateTime.UtcNow);
            }
            finally
            {
                process?.Dispose();
            }

            if (ct.IsCancellationRequested) break;

            State = StreamRecordingState.Backoff;
            var backoffSeconds = Math.Min(60, 2 << Math.Min(consecutiveFailures, 5));
            logger.LogInformation("Motion session backing off {Seconds}s before reconnecting (attempt {Attempt}).", backoffSeconds, consecutiveFailures);
            try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        FlushAll(hysteresis, DateTime.UtcNow);
        State = StreamRecordingState.Idle;
    }

    private void FlushAll(Dictionary<Guid, MotionHysteresis> hysteresis, DateTime nowUtc)
    {
        foreach (var (zoneId, h) in hysteresis)
        {
            var result = h.Flush(nowUtc);
            if (result is not null) MotionSpanCompleted?.Invoke(zoneId, result);
        }
    }

    private async Task WatchdogAsync(Process process, CancellationToken ct)
    {
        while (!process.HasExited && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }

            var staleFor = DateTime.UtcNow - LastFrameAt!.Value;
            if (staleFor.TotalSeconds > options.StalledThresholdSeconds)
            {
                logger.LogWarning("Motion substream stalled ({StaleFor}s since last frame) — restarting ffmpeg.", (int)staleFor.TotalSeconds);
                return;
            }
        }
    }

    private async Task ReadFramesAsync(Process process, Dictionary<Guid, MotionHysteresis> hysteresis, CancellationToken ct)
    {
        var stdout = process.StandardOutput.BaseStream;
        var previous = new byte[_frameSize];
        var current = new byte[_frameSize];
        var havePrevious = false;

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stdout, current, ct)) return; // pipe closed — process exiting

            LastFrameAt = DateTime.UtcNow;
            if (State != StreamRecordingState.Recording) State = StreamRecordingState.Recording;

            if (havePrevious)
            {
                // Score only the Y (luma) plane — the whole frame for the software path (it's all
                // there is), or just the leading Width*Height bytes of a larger nv12 capture buffer
                // on the CUDA path, silently dropping the interleaved UV chroma plane that follows it.
                // See _yPlaneSize's own field comment for why this needs no per-format branch beyond
                // the slice bounds themselves.
                var previousY = previous.AsSpan(0, _yPlaneSize);
                var currentY = current.AsSpan(0, _yPlaneSize);

                var now = DateTime.UtcNow;
                foreach (var zone in zones)
                {
                    var score = MotionDetector.Score(previousY, currentY, zone.Mask, options.PixelDeltaThreshold);
                    var motionPresent = score >= zone.Sensitivity;
                    var result = hysteresis[zone.ZoneId].Observe(now, motionPresent, score);
                    if (result is not null) MotionSpanCompleted?.Invoke(zone.ZoneId, result);
                }
            }

            (previous, current) = (current, previous);
            havePrevious = true;
        }
    }

    // Stream.ReadAsync can return a short read even for a live pipe (no guarantee of filling the
    // buffer in one call) — loops until exactly buffer.Length bytes are read, or the pipe closes
    // (0-byte read) partway through a frame, which is treated as a clean end-of-stream rather than
    // an error: it's the normal shape of ffmpeg exiting mid-frame.
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
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

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return process;
    }

    /// <summary>Pure argument construction — internal for direct testing rather than only exercised
    /// through a spawned ffmpeg process, same reasoning as RecordingSession's own
    /// BuildDecodeArgs/BuildCodecArgs/BuildTeeOutputs split.
    ///
    /// The CUDA branch mirrors VisionSession.StartFfmpeg's own GPU-hybrid decode+scale exactly
    /// (`-hwaccel cuda -hwaccel_output_format cuda` keeps the decoded frame in GPU memory so
    /// scale_cuda actually runs on the GPU, then `hwdownload` brings only the already-downscaled
    /// frame back to host memory) — see that method's doc comment for why nv12 is the confirmed-
    /// working intermediate format (scale_cuda's documented bgra/gray output options exist in its
    /// filter schema but aren't actually implemented by the filter kernel). Deliberately NOT
    /// `format=gray` on this path: asking scale_cuda for a single-plane grayscale output is exactly
    /// the kind of unverified pairing this codebase has already been burned by once (privacy-mask
    /// burn-in's hwaccel-decode-plus-CPU-filter failure) — nv12 is proven, and MotionSession only
    /// ever needs its Y plane anyway (see _yPlaneSize's own comment), so there is no accuracy cost to
    /// carrying the unused chroma plane instead of asking ffmpeg to drop it for us.
    ///
    /// Every other HardwareAcceleration value (including null) is untouched from before this pass —
    /// Intel/AMD keep today's plain software decode until an equivalent verified pairing exists for
    /// them.</summary>
    internal static IReadOnlyList<string> BuildFfmpegArgs(MotionSessionOptions options)
    {
        List<string> args =
        [
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", "5000000",
        ];

        if (UsesNv12(options.HardwareAcceleration))
        {
            args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
            args.AddRange(["-i", options.RtspUri]);
            args.AddRange(["-vf",
                $"fps={options.Fps},scale_cuda=w={options.Width}:h={options.Height}:format=nv12,hwdownload,format=nv12"]);
            args.AddRange(["-fps_mode", "passthrough"]); // see VisionSession.StartFfmpeg's own comment on r_frame_rate duplication
            args.AddRange(["-f", "rawvideo", "-pix_fmt", "nv12", "pipe:1"]);
        }
        else
        {
            args.AddRange(["-i", options.RtspUri]);
            args.AddRange(["-vf", $"fps={options.Fps},scale={options.Width}:{options.Height},format=gray"]);
            args.AddRange(["-f", "rawvideo", "-pix_fmt", "gray", "pipe:1"]);
        }

        return args;
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
            {
                logger.LogDebug("ffmpeg (motion): {Line}", line);

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("could not", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("ffmpeg (motion): {Line}", line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogDebug(ex, "Motion stderr drain ended."); }
    }

    private void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to kill motion ffmpeg process (may have already exited)."); }
    }
}
