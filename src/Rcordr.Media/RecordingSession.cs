using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Rcordr.Core.Enums;

namespace Rcordr.Media;

public record RecordingSessionOptions(
    string FfmpegPath,
    string RtspUri,
    string OutputDirectory,
    int SegmentSeconds = 60,
    /// <summary>A stream with no new segment for this long is considered stalled and the ffmpeg
    /// process is killed so the supervisor loop restarts it — an ffmpeg process that is alive but
    /// has stopped producing output (camera stopped responding mid-connection, network stall) is
    /// the common real-world failure mode, and process liveness alone does not catch it.</summary>
    int StalledThresholdSeconds = 150,
    int PollIntervalSeconds = 5);

/// <summary>
/// Supervises one ffmpeg process recording one camera stream to disk as a sequence of fMP4
/// segments. State machine: Idle -&gt; Connecting -&gt; Recording -&gt; (Failed|Backoff) -&gt; Connecting ...,
/// with exponential backoff between restart attempts. Runs until the CancellationToken passed to
/// <see cref="RunAsync"/> is cancelled.
///
/// Recording is `-c copy` — no transcoding, so this has essentially no CPU cost beyond I/O — and
/// completed segments are detected by polling the output directory rather than a FileSystemWatcher,
/// which is more predictable to reason about for something that has to run unattended for days and
/// is known to be unreliable over SMB-backed paths (a real possibility once storage targets land in
/// M4).
/// </summary>
public sealed class RecordingSession(RecordingSessionOptions options, ILogger logger)
{
    public StreamRecordingState State { get; private set; } = StreamRecordingState.Idle;
    public DateTime? LastSegmentAt { get; private set; }
    public string? LastError { get; private set; }

    public event Action<RecordingSegment>? SegmentCompleted;

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        EnsureUpcomingHourDirectories();
        var consecutiveFailures = 0;
        // Scoped to the whole session, not the per-attempt loop below: it was previously recreated
        // on every ffmpeg restart, which meant every reconnect after a crash rescanned the output
        // directory with no memory of files already reported and re-reported all of them — confirmed
        // in practice as 5-10x duplicate rows for the same handful of files during a crash-loop.
        var reportedPaths = new HashSet<string>();

        while (!ct.IsCancellationRequested)
        {
            State = StreamRecordingState.Connecting;
            Process? process = null;

            try
            {
                process = StartFfmpeg();
                var stderrTask = DrainStderrAsync(process, ct);

                var stalled = false;
                LastSegmentAt = DateTime.UtcNow; // grace period before the first segment lands

                while (!process.HasExited && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), ct); }
                    catch (OperationCanceledException) { break; }

                    // Cheap and idempotent — re-creating an existing directory is a no-op. Run every
                    // poll tick (every 5s by default) rather than once at startup so a session that
                    // keeps the same ffmpeg process alive across an hour boundary (the common case —
                    // ffmpeg only restarts on failure) always has the *next* hour's folder ready well
                    // before it needs it, regardless of how long this attempt has been running.
                    EnsureUpcomingHourDirectories();

                    PollForCompletedSegments(reportedPaths);

                    if (State != StreamRecordingState.Recording && reportedPaths.Count > 0)
                    {
                        State = StreamRecordingState.Recording;
                        consecutiveFailures = 0; // a real segment landed — this attempt succeeded
                    }

                    var staleFor = DateTime.UtcNow - LastSegmentAt!.Value;
                    if (staleFor.TotalSeconds > options.StalledThresholdSeconds)
                    {
                        logger.LogWarning("Stream stalled ({StaleFor}s since last segment) — restarting ffmpeg.", (int)staleFor.TotalSeconds);
                        stalled = true;
                        break;
                    }
                }

                if (stalled && !process.HasExited)
                {
                    TryKill(process);
                }

                await process.WaitForExitAsync(CancellationToken.None);
                await stderrTask;

                FinalizeInProgressSegment(reportedPaths);

                if (ct.IsCancellationRequested) break;

                if (!stalled && process.ExitCode == 0)
                {
                    // Clean exit with code 0 shouldn't normally happen for a live RTSP tee (only
                    // stops on kill/cancel) — treat it as a failure needing backoff rather than a
                    // silent stop, so a camera that closes the connection doesn't quietly stop
                    // recording forever.
                    logger.LogWarning("ffmpeg exited cleanly (code 0) without being asked to — restarting.");
                }
                consecutiveFailures++;
                LastError = $"ffmpeg exited with code {process.ExitCode}.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                LastError = ex.Message;
                logger.LogError(ex, "Recording session failed.");
                if (process is { HasExited: false }) TryKill(process);
            }
            finally
            {
                process?.Dispose();
            }

            if (ct.IsCancellationRequested) break;

            State = StreamRecordingState.Backoff;
            var backoffSeconds = Math.Min(60, 2 << Math.Min(consecutiveFailures, 5)); // 4,8,16,32,60,60...
            logger.LogInformation("Backing off {Seconds}s before reconnecting (attempt {Attempt}).", backoffSeconds, consecutiveFailures);
            try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }

        State = StreamRecordingState.Idle;
    }

    private Process StartFfmpeg()
    {
        // Nested Y/m/d/H folders, not flat: a busy camera at the default 60s segment length writes
        // ~1,440 files/day, and a single flat directory holding tens to hundreds of thousands of
        // files (a realistic 30-90 day retention window) makes every directory listing measurably
        // slower — including this class's own poll every few seconds. ffmpeg's segment muxer has no
        // option to create missing directories on its own (confirmed: no -strftime_mkdir in this
        // build, only -strftime), so EnsureUpcomingHourDirectories() has to stay ahead of it or an
        // hour boundary would abort the whole recording, not just roll to a new folder. The folder
        // names land in the *node's local* time zone (whatever -strftime expands them as), not UTC —
        // deliberately: this path is a human browsing the filesystem, and the timestamps that
        // actually matter for correctness (Segment.StartUtc/EndUtc) are computed independently, in
        // C#, from file metadata — see RecordingSegment's doc comment.
        var outputPattern = $"{options.OutputDirectory.TrimEnd('/', '\\')}/%Y/%m/%d/%H/%Y%m%dT%H%M%SZ.mp4";

        var psi = new ProcessStartInfo
        {
            FileName = options.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            // Explicitly redirected (and never written to) rather than left to inherit the parent's
            // stdin — launched under a supervisor with no real controlling terminal, ffmpeg's
            // interactive keypress handling ('q' to quit, etc.) on an inherited-but-not-really-there
            // stdin was reproduced live as segment rotation silently stopping entirely after the
            // first segment, while the video stream itself kept flowing and growing that one file
            // indefinitely — not a hang, just -segment_atclocktime never firing again. The identical
            // command run from an interactive shell (a real terminal attached) rotated correctly
            // every 60s. -nostdin (belt and suspenders below) is ffmpeg's own documented flag for
            // exactly this headless/service scenario.
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        // -c copy: pure stream copy, no transcode — the recording path never touches a codec.
        // -segment_atclocktime 1 aligns segment boundaries to the wall clock (e.g. :00/:01/:02),
        // matching the plan's tee pipeline design, though live fanout (the other leg of the tee)
        // isn't wired up until M5.
        // -map 0:v -map 0:a?, not -map 0: an ONVIF camera's RTSP session commonly carries a third
        // "Data: none" track alongside video/audio — the metadata stream defined by the profile's
        // MetadataConfiguration (PTZ status / analytics events, not a real recordable stream).
        // Confirmed against a real Amcrest camera: -map 0 pulled that track in, the MP4 muxer has no
        // tag for an unknown-codec data stream, and Could not write header aborted the *entire*
        // segment — losing the video and audio too, even though both were perfectly fine on their
        // own. The "?" on the audio map makes it optional so a camera with no audio track doesn't
        // fail the same way.
        string[] args =
        [
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", "5000000",
            "-i", options.RtspUri,
            "-c", "copy",
            "-map", "0:v",
            "-map", "0:a?",
            "-f", "segment",
            "-segment_time", options.SegmentSeconds.ToString(),
            "-segment_atclocktime", "1",
            "-reset_timestamps", "1",
            "-strftime", "1",
            "-segment_format", "mp4",
            "-segment_format_options", "movflags=+frag_keyframe+empty_moov",
            "-y", outputPattern
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        logger.LogInformation("Starting ffmpeg for {RtspUri} -> {OutputDirectory}", RedactCredentials(options.RtspUri), options.OutputDirectory);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return process;
    }

    private void PollForCompletedSegments(HashSet<string> reportedPaths)
    {
        string[] files;
        try
        {
            // AllDirectories: segments now live under nested Y/m/d/H folders, not flat in
            // OutputDirectory. Full paths still sort chronologically as strings — the folder
            // hierarchy is itself zero-padded and chronological (2026/08/09/14/... sorts before
            // .../15/...), same as the zero-padded filename underneath it.
            files = Directory.GetFiles(options.OutputDirectory, "*.mp4", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
        }
        catch (IOException)
        {
            return; // directory transiently unavailable (e.g. SMB hiccup) — try again next poll
        }

        // Every file except the last is guaranteed closed: ffmpeg's segment muxer only ever has one
        // file open for writing at a time and creates the next before finishing with the last, so a
        // file's existence alongside a newer one is proof it's done.
        for (var i = 0; i < files.Length - 1; i++)
        {
            var path = files[i];
            if (!reportedPaths.Add(path)) continue;

            FileInfo info;
            try { info = new FileInfo(path); }
            catch (IOException) { reportedPaths.Remove(path); continue; }

            // ffmpeg opens (creates/truncates) the segment file as part of starting a header write,
            // before it can fail that write — confirmed in practice: a camera whose RTSP session
            // included a track the muxer rejected left a trail of these behind, one per failed
            // reconnect attempt, until the underlying cause was fixed. A 0-byte file is that failure
            // artifact, never a real segment, and must not be reported (or count toward
            // LastSegmentAt — an endless loop of instant failures would otherwise look "healthy" to
            // the stall watchdog).
            if (info.Length == 0) continue;

            var start = info.CreationTimeUtc;
            var end = new FileInfo(files[i + 1]).CreationTimeUtc;
            LastSegmentAt = DateTime.UtcNow;
            SegmentCompleted?.Invoke(new RecordingSegment(path, start, end, info.Length));
        }
    }

    /// <summary>Called after the process has exited (clean stop, crash, or watchdog kill) — the
    /// last file ffmpeg was writing when it stopped is still a real, playable segment (frag_keyframe
    /// + empty_moov survives a truncated write), so it is reported too rather than silently dropped.</summary>
    private void FinalizeInProgressSegment(HashSet<string> reportedPaths)
    {
        string[] files;
        try { files = Directory.GetFiles(options.OutputDirectory, "*.mp4", SearchOption.AllDirectories); }
        catch (IOException) { return; }
        if (files.Length == 0) return;

        Array.Sort(files, StringComparer.Ordinal);
        var last = files[^1];
        if (reportedPaths.Contains(last)) return;

        FileInfo info;
        try { info = new FileInfo(last); }
        catch (IOException) { return; }
        if (info.Length == 0) return; // never got any data — not a real segment

        SegmentCompleted?.Invoke(new RecordingSegment(last, info.CreationTimeUtc, DateTime.UtcNow, info.Length));
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct)) is not null)
            {
                // Always available at Debug so a log-level change can see full detail without a
                // code change. ffmpeg logs routine progress to stderr by design, so only a broader
                // set of failure-shaped lines is escalated to Warning — narrowed to "error"/"failed"
                // alone previously missed the actual root cause of a real failure ("Could not write
                // header (incorrect codec parameters?): Invalid argument"), leaving only the generic
                // "Conversion failed!" summary line visible in the log.
                logger.LogDebug("ffmpeg: {Line}", line);
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("could not", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("unable to", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("ffmpeg: {Line}", line);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "stderr drain ended.");
        }
    }

    /// <summary>Pre-creates the current and next hour's output folder. Local time, matching
    /// whatever -strftime will actually expand to — see StartFfmpeg's comment on why the folder
    /// names are local rather than UTC. Creating a directory that already exists is a no-op, so
    /// this is safe (and cheap) to call on every poll tick rather than only at startup.</summary>
    private void EnsureUpcomingHourDirectories()
    {
        var now = DateTime.Now;
        Directory.CreateDirectory(HourDirectory(now));
        Directory.CreateDirectory(HourDirectory(now.AddHours(1)));
    }

    private string HourDirectory(DateTime when)
        => Path.Combine(options.OutputDirectory,
            when.ToString("yyyy"), when.ToString("MM"), when.ToString("dd"), when.ToString("HH"));

    private void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { logger.LogDebug(ex, "Failed to kill ffmpeg process (may have already exited)."); }
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
