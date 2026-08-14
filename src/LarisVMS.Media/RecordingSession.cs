using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Enums;

namespace LarisVMS.Media;

/// <summary>Real resolution/codec parsed from ffmpeg's own stderr when it opens the input stream —
/// more trustworthy than ONVIF's advertised VideoEncoderConfiguration, which some cameras (confirmed:
/// Amcrest, on H.265 profiles) omit from GetProfiles/GetVideoEncoderConfiguration entirely.</summary>
public record StreamResolution(int Width, int Height, string? Codec);

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
    public event Action<StreamResolution>? StreamResolutionDetected;

    /// <summary>The most recent complete fMP4 init segment (ftyp+moov) from the live tee leg — a
    /// late-joining live viewer needs exactly these bytes before any fragment. Replaced (not
    /// appended to) each time ffmpeg (re)starts, since a new process means a new moov. Null until the
    /// current attempt's ffmpeg process has produced one.</summary>
    public byte[]? LiveInitSegment { get; private set; }

    /// <summary>Raised for each chunk of live fragment (moof+mdat) bytes after LiveInitSegment is
    /// available — never raised for the init segment's own bytes, callers read LiveInitSegment
    /// directly for that. Each array is a fresh copy safe to hold onto past the event call.</summary>
    public event Action<byte[]>? LiveFragmentReceived;

    // A late-joining live viewer needs to wait for the *current* attempt's init segment rather than
    // poll LiveInitSegment — replaced at the start of every attempt (not just once) so a viewer that
    // connects while ffmpeg is mid-restart waits for the new attempt's moov instead of hanging on a
    // task that will never complete (the old attempt is gone, its TCS is cancelled below).
    private TaskCompletionSource<byte[]> _initSegmentTcs = NewInitSegmentTcs();
    private static TaskCompletionSource<byte[]> NewInitSegmentTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits for the current ffmpeg attempt's live init segment. Throws
    /// OperationCanceledException if ct fires (typically a caller-supplied timeout) before one
    /// arrives, or if this attempt ends (crash, stop, restart) before producing one.</summary>
    public Task<byte[]> WaitForLiveInitSegmentAsync(CancellationToken ct) => _initSegmentTcs.Task.WaitAsync(ct);

    // ffmpeg prints each input stream's summary once, right after it opens the RTSP connection, e.g.
    // "Stream #0:0: Video: hevc (Main), yuv420p(tv, bt709), 2560x1440, 15 fps, ...". Matches the
    // codec name and the first WxH pair on that line — good enough since ffmpeg's own format for this
    // line hasn't changed across the versions this has been tested against.
    private static readonly Regex VideoStreamLine = new(
        @"Stream #\d+:\d+.*?Video:\s*([A-Za-z0-9_]+).*?(\d{2,5})x(\d{2,5})",
        RegexOptions.Compiled);

    /// <summary>Pure parsing extracted from DrainStderrAsync so it's unit-testable against real
    /// captured ffmpeg output lines without spawning a process.</summary>
    internal static StreamResolution? TryParseVideoStreamLine(string line)
    {
        var match = VideoStreamLine.Match(line);
        if (!match.Success) return null;
        return new StreamResolution(int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), match.Groups[1].Value);
    }

    /// <summary><paramref name="knownPaths"/> pre-seeds the reported-paths set with segment files
    /// the server already has a Segments row for, from the caller's own record (NodeWorker fetches
    /// this once via GET /api/nodes/segments/paths before any session starts) — without it, a
    /// *process* restart (not just an ffmpeg reconnect, which reportedPaths already survives — see
    /// its own comment below) rescans this camera's entire on-disk history with zero memory of
    /// what's already been reported, and every one of those re-discovered files fires
    /// SegmentCompleted again as if brand new. Confirmed as a real, serious bug, not theoretical:
    /// for a Motion-mode camera, a freshly-restarted MotionSession/CameraEventSession has observed
    /// no motion yet at the moment that rescan runs, so nearly all of that re-fired history looked
    /// like "no motion" to NodeWorker.DecideMotionSegment and was wrongly discarded — deleting files
    /// that already had valid, previously-reported Segments rows, on every single node restart.</summary>
    public async Task RunAsync(CancellationToken ct, IEnumerable<string>? knownPaths = null)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        EnsureUpcomingHourDirectories();
        var consecutiveFailures = 0;
        // Scoped to the whole session, not the per-attempt loop below: it was previously recreated
        // on every ffmpeg restart, which meant every reconnect after a crash rescanned the output
        // directory with no memory of files already reported and re-reported all of them — confirmed
        // in practice as 5-10x duplicate rows for the same handful of files during a crash-loop.
        // Case-insensitive: Windows paths (local or UNC) are case-insensitive/preserving, and
        // knownPaths comes back from the server exactly as originally reported, which could differ
        // in case from how this run's own Directory.GetFiles happens to return the same path.
        var reportedPaths = new HashSet<string>(knownPaths ?? [], StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            State = StreamRecordingState.Connecting;
            Process? process = null;

            try
            {
                process = StartFfmpeg();
                var stderrTask = DrainStderrAsync(process, ct);
                // A new process means a new moov — the old one (and anyone still waiting on it) is
                // no longer valid.
                LiveInitSegment = null;
                if (!_initSegmentTcs.Task.IsCompleted) _initSegmentTcs.TrySetCanceled();
                _initSegmentTcs = NewInitSegmentTcs();
                var stdoutTask = DrainStdoutAsync(process, ct);

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
                await stdoutTask;

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

        // Session is ending for good (camera reassigned, node stopping, ...) — a viewer still
        // waiting on an init segment that's never coming needs to be released, not left hanging.
        if (!_initSegmentTcs.Task.IsCompleted) _initSegmentTcs.TrySetCanceled();

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
            // The live leg of the tee (below) writes fMP4 fragments to pipe:1 — this is now read
            // continuously by DrainStdoutAsync, not left unredirected.
            RedirectStandardOutput = true,
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
        // -c copy: pure stream copy, no transcode — this whole pipeline, recording and live alike,
        // never touches a codec.
        // -map 0:v -map 0:a?, not -map 0: an ONVIF camera's RTSP session commonly carries a third
        // "Data: none" track alongside video/audio — the metadata stream defined by the profile's
        // MetadataConfiguration (PTZ status / analytics events, not a real recordable stream).
        // Confirmed against a real Amcrest camera: -map 0 pulled that track in, the MP4 muxer has no
        // tag for an unknown-codec data stream, and Could not write header aborted the *entire*
        // segment — losing the video and audio too, even though both were perfectly fine on their
        // own. The "?" on the audio map makes it optional so a camera with no audio track doesn't
        // fail the same way. Mapped once, ahead of -f tee, so both legs inherit the same mapping.
        //
        // -f tee fans the one RTSP connection out to two muxers so recording and live view never
        // need a second session against the camera (some cameras cap concurrent RTSP sessions quite
        // low) — matches the plan's original pipeline design:
        //   leg 1: the segment muxer writing the files kept on disk (segment_atclocktime aligns
        //   boundaries to the wall clock; frag_keyframe+empty_moov means a segment truncated by
        //   power loss is still playable).
        //   leg 2: a single continuous fMP4 stream to pipe:1 (this process's own stdout).
        // Both legs need default_base_moof, for the same reason: without it a fragment's moof
        // describes sample offsets relative to the *file*, which only makes sense once, at the
        // start; MSE needs each fragment self-contained, offsets relative to its own moof. This was
        // originally set only on leg 2, back when the live viewer was the only MSE consumer and the
        // recorded files were just files. M7's playback then started feeding those same files to
        // MSE and inherited a latent bug: Chrome *accepts* the append, fires updateend normally, and
        // produces no buffered range whatsoever — no error, no event — so a tile rendered nothing
        // while looking exactly like one still loading. Verified by parsing the tfhd flags out of
        // both legs' real output: leg 2 had 0x020038 (default-base-is-moof set), leg 1 had 0x000039
        // (base-data-offset-present instead), and adding the flag makes leg 1 byte-structurally
        // match leg 2. (Note the flag name: verified against this build's own `-h muxer=mov` output
        // as "default_base_moof" — "default_base_is_moof", the name used in ffmpeg's own CLI docs
        // and this project's architecture plan, is not accepted by this muxer and silently aborts
        // the *entire* tee, both legs, the moment ffmpeg tries to write the first header — worth
        // remembering if a future ffmpeg version renames it again.)
        // Tee's own bracket syntax uses ':' as its option separator, which collides with a Windows
        // drive letter (e.g. "C:\...") in the segment output path — escaped below, tee-syntax only,
        // never touching the path actually handed to the filesystem.
        var teeOutputs = BuildTeeOutputs(options.SegmentSeconds, outputPattern);

        string[] args =
        [
            "-nostdin",
            "-rtsp_transport", "tcp",
            "-timeout", "5000000",
            "-i", options.RtspUri,
            "-c", "copy",
            "-map", "0:v",
            "-map", "0:a?",
            "-f", "tee",
            "-y", teeOutputs
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        logger.LogInformation("Starting ffmpeg for {RtspUri} -> {OutputDirectory}", RedactCredentials(options.RtspUri), options.OutputDirectory);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return process;
    }

    /// <summary>internal, not private: unit-tested directly against a real temp directory (see
    /// LarisVMS.Tests) to prove a pre-seeded reportedPaths entry is skipped rather than re-firing
    /// SegmentCompleted — the actual bug fix in RunAsync's knownPaths parameter, exercised here
    /// without needing to spawn a real ffmpeg process.</summary>
    internal void PollForCompletedSegments(HashSet<string> reportedPaths)
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
        var resolutionReported = false;
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

                if (!resolutionReported && TryParseVideoStreamLine(line) is { } resolution)
                {
                    resolutionReported = true;
                    StreamResolutionDetected?.Invoke(resolution);
                }

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

    /// <summary>Reads the live tee leg continuously from ffmpeg's stdout. Buffers leading bytes
    /// until Mp4BoxScanner finds the end of the init segment (ftyp+moov), publishes that once as
    /// LiveInitSegment, then raises LiveFragmentReceived for every chunk after — deliberately never
    /// blocks on a subscriber (LiveFragmentReceived handlers are expected to be non-blocking, e.g.
    /// a bounded channel's TryWrite) since this loop is also what keeps ffmpeg's stdout pipe drained;
    /// a slow live viewer must never be able to back-pressure the recording leg of the same tee.</summary>
    private async Task DrainStdoutAsync(Process process, CancellationToken ct)
    {
        // Generous but bounded: an init segment (ftyp+moov, no sample data — empty_moov) is a few
        // hundred bytes to a few KB in practice. If moov genuinely never arrives within this many
        // bytes something is wrong with the stream, and waiting forever would leak memory silently.
        const int MaxInitSegmentBytes = 4 * 1024 * 1024;

        var stream = process.StandardOutput.BaseStream;
        var readBuffer = new byte[64 * 1024];
        using var initBuffer = new MemoryStream();
        var initSegmentResolved = false;

        try
        {
            int read;
            while ((read = await stream.ReadAsync(readBuffer, ct)) > 0)
            {
                if (!initSegmentResolved)
                {
                    initBuffer.Write(readBuffer, 0, read);

                    if (initBuffer.Length > MaxInitSegmentBytes)
                    {
                        logger.LogWarning("Live init segment exceeded {MaxBytes} bytes without a complete moov box — giving up on live view for this attempt.", MaxInitSegmentBytes);
                        initSegmentResolved = true; // stop trying; recording itself is unaffected
                        continue;
                    }

                    var end = Mp4BoxScanner.TryFindInitSegmentEnd(initBuffer.GetBuffer().AsSpan(0, (int)initBuffer.Length));
                    if (end is null) continue;

                    initSegmentResolved = true;
                    LiveInitSegment = initBuffer.GetBuffer()[..end.Value];
                    _initSegmentTcs.TrySetResult(LiveInitSegment);

                    // Anything captured past the init segment boundary in this same read is already
                    // the start of the first fragment — forward it now rather than waiting for the
                    // next stream.ReadAsync, or that fragment's leading bytes would be silently lost.
                    var trailing = (int)initBuffer.Length - end.Value;
                    if (trailing > 0)
                    {
                        var chunk = new byte[trailing];
                        Array.Copy(initBuffer.GetBuffer(), end.Value, chunk, 0, trailing);
                        LiveFragmentReceived?.Invoke(chunk);
                    }
                    continue;
                }

                var fragment = new byte[read];
                Array.Copy(readBuffer, fragment, read);
                LiveFragmentReceived?.Invoke(fragment);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "stdout (live) drain ended.");
        }
    }

    /// <summary>Prepares a path for ffmpeg's tee muxer bracket syntax. Two escaping rules, applied
    /// in this order (verified locally against a real tee command before this ever reached a
    /// recording node — reversing the order silently corrupts the whole path, confirmed):
    /// (1) backslashes to forward slashes first — tee's bracket-option parser treats '\' as its own
    /// generic escape character, so a raw Windows path's separators get silently eaten, e.g.
    /// "C:\Users\..." becomes "C:Users..." with every backslash-prefixed character mangled; ffmpeg
    /// accepts forward slashes in Windows paths just fine, sidestepping this entirely; (2) only then
    /// escape ':' — tee's own option separator, colliding with a Windows drive letter — as '\:',
    /// which is now unambiguous since no other backslashes remain in the string.
    /// Only ever applied to the string embedded in the tee argument, never to the path actually used
    /// for filesystem operations elsewhere in this class.</summary>
    /// <summary>Builds the -f tee output spec (both legs). Extracted so the muxer flags can be
    /// asserted directly in a unit test — the bug this guards against was a flag present on one leg
    /// and missing from the other, which produced no error anywhere and only surfaced as a browser
    /// rendering nothing.</summary>
    internal static string BuildTeeOutputs(int segmentSeconds, string outputPattern) => string.Join('|',
        $"[f=segment:segment_time={segmentSeconds}:segment_atclocktime=1:reset_timestamps=1:strftime=1:" +
        $"segment_format=mp4:segment_format_options=movflags={MseMovFlags}]{EscapeForTee(outputPattern)}",
        $"[f=mp4:movflags={MseMovFlags}]pipe:1");

    /// <summary>The mov muxer flags every leg must use. Both legs are consumed by MSE (live view
    /// reads the pipe, playback reads the recorded files), and MSE requires default_base_moof on
    /// both — see the commentary in StartFfmpeg for what happens when a leg is missing it.</summary>
    private const string MseMovFlags = "+frag_keyframe+empty_moov+default_base_moof";

    private static string EscapeForTee(string path) => path.Replace('\\', '/').Replace(":", "\\:");

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
