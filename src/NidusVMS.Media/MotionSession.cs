using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NidusVMS.Core.Enums;

namespace NidusVMS.Media;

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
    int StalledThresholdSeconds = 30)
{
    public TimeSpan StartAfter { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan EndAfter { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Supervises one ffmpeg process reading a camera's substream as a fixed-size grayscale rawvideo
/// pipe, diffing consecutive frames per zone (MotionDetector), and turning threshold crossings into
/// discrete spans (MotionHysteresis) — one hysteresis instance per zone, since each zone's motion
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

    private readonly int _frameSize = options.Width * options.Height;

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
    /// This is the query that makes both pre-roll and post-roll fall out of one comparison, exactly
    /// because LastMotionAtUtc only ever moves forward (see its doc comment). NodeWorker defers a
    /// segment's keep/discard decision until PreRoll seconds after that segment ends, then asks
    /// HasMotionSince(segment.EndUtc - PostRoll) at that later moment: if motion happened anywhere
    /// from PostRoll *before* the segment ended through PreRoll *after* it ended, this is true,
    /// covering "we're in the post-roll tail of an earlier event" and "this segment turned out to be
    /// the pre-roll for an event that hadn't started yet when it completed" with the same call.</summary>
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
                var now = DateTime.UtcNow;
                foreach (var zone in zones)
                {
                    var score = MotionDetector.Score(previous, current, zone.Mask, options.PixelDeltaThreshold);
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

        string[] args =
        [
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", "5000000",
            "-i", options.RtspUri,
            "-vf", $"fps={options.Fps},scale={options.Width}:{options.Height},format=gray",
            "-f", "rawvideo",
            "-pix_fmt", "gray",
            "pipe:1"
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return process;
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
