using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using LarisVMS.Core.Enums;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Media;

public record SubLiveSessionOptions(
    string FfmpegPath,
    string RtspUri,
    int StalledThresholdSeconds = 150,
    int PollIntervalSeconds = 5);

/// <summary>
/// M18 (adaptive streaming): supervises one ffmpeg process pulling a camera's Sub stream and
/// re-muxing it into the same MSE-fragmented fMP4 shape RecordingSession's live tee leg produces —
/// so a small live tile can be served Sub instead of Main, cutting bandwidth/decode cost for a tile
/// too small to show Main's extra resolution anyway. Nothing here is recorded to disk; Sub is not,
/// and has never been, a recording source (see NodeWorker's own "recording only ever uses the Main
/// stream" comment) — this class exists purely to serve live viewers.
///
/// Deliberately its own class rather than a RecordingSession running against a different RtspUri:
/// RecordingSession's shape (segment muxing, tee, retention-facing SegmentCompleted, stderr
/// resolution/audio parsing for the Segments-table columns) is entirely about recording, none of
/// which applies here. Sharing the small "read stdout, find MP4 box boundaries, publish fragments"
/// piece would mean extracting it out of an already-working, heavily-tested, production-critical
/// class for a first pass of a new, unverified-live feature — MotionSession already sets the
/// precedent for "supervise a second independent ffmpeg process against Sub, duplicating the small
/// process-supervision skeleton rather than touching RecordingSession," and this follows the same
/// call. The MP4-box-boundary-finding itself *is* shared, via Mp4BoxScanner, which was always a
/// separate, recording-agnostic class.
///
/// Lifecycle: always-on whenever NodeWorker decides one should exist (adaptive streaming enabled
/// system-wide, camera has an enabled Sub stream, camera is actively assigned/recording on this
/// node) — not started/stopped per viewer. Simpler and consistent with every other session in this
/// app (RecordingSession, MotionSession) being "on while its conditions hold," at the cost of
/// pulling Sub continuously even with nobody currently watching. Sub streams are typically a small
/// fraction of Main's bitrate (this is exactly what MotionSession already does today for every
/// ServerMotion-zoned camera, with no reported cost concern), so this is judged an acceptable trade
/// for a first pass; on-demand start-on-first-viewer/stop-on-last-viewer has no precedent in this
/// codebase to build on and is a reasonable future optimization if it ever proves necessary.
///
/// Same Idle -&gt; Connecting -&gt; Recording -&gt; Backoff shape as RecordingSession/MotionSession,
/// simplified: no segment files, no tee (a single -c copy leg has nothing to fan out to), no stderr
/// resolution/audio parsing (Sub's Width/Height/Codec/HasAudio are already known from the Camera's
/// own probed CameraStream row — this class doesn't need to rediscover them the way RecordingSession
/// does for Main, which predates per-stream probing).
/// </summary>
public sealed class SubLiveSession(SubLiveSessionOptions options, ILogger logger) : ILiveSource
{
    public StreamRecordingState State { get; private set; } = StreamRecordingState.Idle;
    public string? LastError { get; private set; }

    /// <summary>Same shape/reasoning as RecordingSession.LiveInitSegment — the most recent complete
    /// fMP4 init segment (ftyp+moov), replaced each time ffmpeg (re)starts.</summary>
    public byte[]? LiveInitSegment { get; private set; }

    public event Action<byte[]>? LiveFragmentReceived;

    private TaskCompletionSource<byte[]> _initSegmentTcs = NewInitSegmentTcs();
    private static TaskCompletionSource<byte[]> NewInitSegmentTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<byte[]> WaitForLiveInitSegmentAsync(CancellationToken ct) => _initSegmentTcs.Task.WaitAsync(ct);

    public async Task RunAsync(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        var lastFragmentAt = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            State = StreamRecordingState.Connecting;
            Process? process = null;

            try
            {
                process = StartFfmpeg();
                var stderrTask = DrainStderrAsync(process, ct);

                LiveInitSegment = null;
                if (!_initSegmentTcs.Task.IsCompleted) _initSegmentTcs.TrySetCanceled();
                _initSegmentTcs = NewInitSegmentTcs();
                lastFragmentAt = DateTime.UtcNow; // grace period before the first fragment lands

                var stdoutTask = DrainStdoutAsync(process, ct, () => lastFragmentAt = DateTime.UtcNow);
                var watchdogTask = WatchdogAsync(process, ct, () => lastFragmentAt);

                await Task.WhenAny(stdoutTask, watchdogTask);
                if (!process.HasExited) TryKill(process);

                await process.WaitForExitAsync(CancellationToken.None);
                await stderrTask;
                try { await stdoutTask; } catch (OperationCanceledException) { }

                if (ct.IsCancellationRequested) break;

                consecutiveFailures++;
                LastError = $"ffmpeg exited with code {process.ExitCode}.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                LastError = ex.Message;
                logger.LogError(ex, "Sub live session failed.");
                if (process is { HasExited: false }) TryKill(process);
            }
            finally
            {
                process?.Dispose();
            }

            if (ct.IsCancellationRequested) break;

            State = StreamRecordingState.Backoff;
            var backoffSeconds = Math.Min(60, 2 << Math.Min(consecutiveFailures, 5));
            logger.LogInformation("Sub live session backing off {Seconds}s before reconnecting (attempt {Attempt}).", backoffSeconds, consecutiveFailures);
            try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        if (!_initSegmentTcs.Task.IsCompleted) _initSegmentTcs.TrySetCanceled();
        State = StreamRecordingState.Idle;
    }

    private async Task WatchdogAsync(Process process, CancellationToken ct, Func<DateTime> lastFragmentAt)
    {
        while (!process.HasExited && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), ct); }
            catch (OperationCanceledException) { return; }

            var staleFor = DateTime.UtcNow - lastFragmentAt();
            if (staleFor.TotalSeconds > options.StalledThresholdSeconds)
            {
                logger.LogWarning("Sub live stream stalled ({StaleFor}s since last fragment) — restarting ffmpeg.", (int)staleFor.TotalSeconds);
                return;
            }
        }
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

        // -c copy: Sub arrives from the camera already encoded at a resolution/bitrate the camera
        // itself chose for exactly this purpose — nothing here re-encodes it. No -f tee: a single
        // leg has nothing to fan out to, unlike RecordingSession's Main pipeline which also writes
        // segment files. Same MseMovFlags as RecordingSession's own live leg — both are read by the
        // identical Mp4BoxScanner-based drain below, and MSE requires default_base_moof either way.
        string[] args =
        [
            "-nostdin", "-rtsp_transport", "tcp", "-timeout", "5000000",
            "-i", options.RtspUri,
            "-map", "0:v", "-map", "0:a?",
            "-c", "copy",
            "-f", "mp4", "-movflags", MseMovFlags,
            "pipe:1"
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        logger.LogInformation("Starting Sub live ffmpeg for {RtspUri}", RedactCredentials(options.RtspUri));

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return process;
    }

    // "+frag_keyframe+empty_moov+default_base_moof" — same flags, same constant value, as
    // RecordingSession.MseMovFlags. Not shared by reference (that one is private to RecordingSession)
    // deliberately: two independent literals are two things to keep in sync if MSE's requirements
    // ever change, which is judged an acceptable trade against reaching into RecordingSession's own
    // internals for a constant. Both are exercised by the exact same real browser MSE consumer
    // (live-view.js), so any drift here would surface immediately as "this tile never plays."
    private const string MseMovFlags = "+frag_keyframe+empty_moov+default_base_moof";

    /// <summary>Same box-boundary-driven drain as RecordingSession.DrainStdoutAsync, minus the tee/
    /// recording concerns — this is the whole output, not one leg of it. See that method's own
    /// comment for why PipeReader/AdvanceTo(consumed, examined) is used instead of a hand-rolled
    /// buffer.</summary>
    private async Task DrainStdoutAsync(Process process, CancellationToken ct, Action onFragment)
    {
        const int MaxInitSegmentBytes = 4 * 1024 * 1024;
        const int MaxFragmentBytes = 32 * 1024 * 1024;

        var reader = PipeReader.Create(process.StandardOutput.BaseStream,
            new StreamPipeReaderOptions(leaveOpen: true, bufferSize: 64 * 1024));
        var initSegmentResolved = false;

        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(ct);
                var buffer = result.Buffer;
                var consumed = buffer.Start;

                if (!initSegmentResolved)
                {
                    var initEnd = Mp4BoxScanner.TryFindInitSegmentEnd(buffer);
                    if (initEnd is null)
                    {
                        if (buffer.Length > MaxInitSegmentBytes)
                        {
                            logger.LogWarning("Sub live init segment exceeded {MaxBytes} bytes without a complete moov box — giving up on this attempt.", MaxInitSegmentBytes);
                            initSegmentResolved = true;
                            reader.AdvanceTo(buffer.End);
                            continue;
                        }
                        reader.AdvanceTo(buffer.Start, buffer.End);
                        if (result.IsCompleted) break;
                        continue;
                    }

                    initSegmentResolved = true;
                    LiveInitSegment = buffer.Slice(0, initEnd.Value).ToArray();
                    _initSegmentTcs.TrySetResult(LiveInitSegment);
                    consumed = buffer.GetPosition(initEnd.Value);
                }

                while (true)
                {
                    var remaining = buffer.Slice(consumed);
                    var length = Mp4BoxScanner.TryFindFragmentEnd(remaining);
                    if (length is null)
                    {
                        if (remaining.Length > MaxFragmentBytes)
                        {
                            logger.LogWarning("Sub live fragment exceeded {MaxBytes} bytes without a complete moof+mdat — dropping the buffer.", MaxFragmentBytes);
                            consumed = buffer.End;
                        }
                        break;
                    }

                    onFragment();
                    var subscribers = LiveFragmentReceived;
                    subscribers?.Invoke(remaining.Slice(0, length.Value).ToArray());

                    consumed = buffer.GetPosition(length.Value, consumed);
                }

                reader.AdvanceTo(consumed, buffer.End);
                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sub live stdout drain ended.");
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
            {
                logger.LogDebug("ffmpeg (sub live): {Line}", line);

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("could not", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("ffmpeg (sub live): {Line}", line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogDebug(ex, "Sub live stderr drain ended."); }
    }

    private void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to kill sub-live ffmpeg process (may have already exited)."); }
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
