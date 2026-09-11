using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Enums;

namespace LarisVMS.Media;

/// <summary>Real resolution/codec parsed from ffmpeg's own stderr when it opens the input stream —
/// more trustworthy than ONVIF's advertised VideoEncoderConfiguration, which some cameras (confirmed:
/// Amcrest, on H.265 profiles) omit from GetProfiles/GetVideoEncoderConfiguration entirely.</summary>
public record StreamResolution(int Width, int Height, string? Codec);

/// <summary>The input's audio track as ffmpeg itself describes it when it opens the stream — codec
/// name plus sample rate. Same reasoning as <see cref="StreamResolution"/>: this is what is actually
/// arriving on the wire, whereas ONVIF's AudioEncoderConfiguration is an advertised capability that
/// cameras are inconsistent about (and which this app never read in the first place — CameraStream's
/// AudioCodec column existed unpopulated until this became the thing that fills it). SampleRateHz is
/// null when ffmpeg's own line doesn't carry a rate, rather than guessing a default.</summary>
public record StreamAudioInfo(string? Codec, int? SampleRateHz);

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
    int PollIntervalSeconds = 5,
    /// <summary>How long RunAsync waits for a killed ffmpeg process (and its stdout/stderr drain
    /// tasks, which only complete once the process's own pipes close) to actually finish before
    /// giving up on waiting and moving straight to backoff/reconnect instead. Confirmed live
    /// (2026-08-21) as a real, serious gap without this bound: a process blocked on an
    /// uninterruptible kernel-mode I/O wait — this fleet's SMB-backed storage dropping mid-write —
    /// can leave Process.Kill's TerminateProcess taking a long time, sometimes far longer than this
    /// timeout, to actually take effect, since the OS still has to unwind that stuck I/O first.
    /// Recording sat completely frozen at this exact wait for a 2+ hour outage: never reaching the
    /// reconnect logic below, never starting a fresh attempt, until a full node restart forced it.
    /// See RunAsync's own handling for what happens when this elapses — the old process is abandoned
    /// (left to exit on its own whenever its stuck I/O clears) rather than blocked on forever.</summary>
    int KillTimeoutSeconds = 15,
    /// <summary>M18: `-vf drawbox=...` expressions from PrivacyMaskFilterBuilder, one per enabled
    /// Privacy zone on this camera. Null/empty (the default, and every camera with no Privacy zone)
    /// keeps the original `-c copy` pipeline exactly as before this feature existed — burning in a
    /// mask requires a real decode+encode, so a camera with nothing to mask must not pay that cost.</summary>
    IReadOnlyList<string>? PrivacyMaskFilters = null,
    /// <summary>The encoder to transcode with when PrivacyMaskFilters is non-empty — from
    /// EncoderSelection.ChooseH264Encoder against this node's own detected capabilities. Ignored
    /// entirely when PrivacyMaskFilters is empty. Null with a non-empty PrivacyMaskFilters is treated
    /// as "fall back to libx264" (see StartFfmpeg) rather than silently keeping `-c copy` — a camera
    /// with a configured privacy zone must never record unmasked footage because of a missing
    /// encoder value.</summary>
    string? VideoEncoder = null);

/// <summary>
/// Supervises one ffmpeg process recording one camera stream to disk as a sequence of fMP4
/// segments. State machine: Idle -&gt; Connecting -&gt; Recording -&gt; (Failed|Backoff) -&gt; Connecting ...,
/// with exponential backoff between restart attempts. Runs until the CancellationToken passed to
/// <see cref="RunAsync"/> is cancelled.
///
/// Recording is `-c copy` by default — no transcoding, so this has essentially no CPU cost beyond
/// I/O — unless RecordingSessionOptions.PrivacyMaskFilters is non-empty (M18), in which case it's a
/// real decode/filter/encode instead, for exactly the cameras that need a privacy mask burned in and
/// none of the others. Completed segments are detected by polling the output directory rather than a
/// FileSystemWatcher,
/// which is more predictable to reason about for something that has to run unattended for days and
/// is known to be unreliable over SMB-backed paths (a real possibility once storage targets land in
/// M4).
/// </summary>
public sealed class RecordingSession(RecordingSessionOptions options, ILogger logger) : ILiveSource
{
    public StreamRecordingState State { get; private set; } = StreamRecordingState.Idle;
    public DateTime? LastSegmentAt { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>M11: most recently observed values from ffmpeg's own periodic progress line (the
    /// same "frame=... fps=... bitrate=..." stats line ffmpeg prints to stderr for any run, copy or
    /// transcode) — real throughput, not a static capability like StreamResolutionDetected's
    /// once-per-connection Width/Height/Codec. Null until the first progress line arrives after
    /// connecting, and again after every reconnect (a fresh RecordingSession attempt starts these
    /// back at null rather than carrying the previous attempt's now-stale numbers forward).</summary>
    public int? CurrentFps { get; private set; }
    public int? CurrentBitrateKbps { get; private set; }

    /// <summary>M11: cumulative count of failed/backoff attempts since this RecordingSession was
    /// created — unlike the local `consecutiveFailures` variable in RunAsync (which exists purely to
    /// drive backoff timing and resets to 0 on every successful segment), this never resets, so a
    /// camera that reconnects constantly through the day is visible as "many total reconnects" even
    /// though each individual streak is short. A dropped-frames counter was considered too (the plan
    /// doc's original health-signal sketch mentions one) but at the time this pipeline was `-c copy`
    /// throughout — no decode ever happened, so there was no meaningful "dropped frame" for ffmpeg to
    /// report; it was left out rather than faked. M18's privacy-mask transcode path is a real decode
    /// now, so a real dropped-frame count is possible for a masked camera — not added here yet.</summary>
    public int TotalReconnectCount { get; private set; }

    public event Action<RecordingSegment>? SegmentCompleted;
    public event Action<StreamResolution>? StreamResolutionDetected;

    /// <summary>Fires once per ffmpeg connection for an input that has an audio track, right after
    /// ffmpeg prints its stream summary — never fires at all for a video-only camera, which is what
    /// leaves the audio columns null for those.</summary>
    public event Action<StreamAudioInfo>? StreamAudioDetected;

    /// <summary>The most recent complete fMP4 init segment (ftyp+moov) from the live tee leg — a
    /// late-joining live viewer needs exactly these bytes before any fragment. Replaced (not
    /// appended to) each time ffmpeg (re)starts, since a new process means a new moov. Null until the
    /// current attempt's ffmpeg process has produced one.</summary>
    public byte[]? LiveInitSegment { get; private set; }

    /// <summary>Raised once per <em>complete</em> fMP4 fragment (moof+mdat) after LiveInitSegment is
    /// available — never raised for the init segment's own bytes, callers read LiveInitSegment
    /// directly for that. Each array is a fresh copy safe to hold onto past the event call.
    ///
    /// Whole fragments, not raw pipe reads: a subscriber may join at any moment and may drop under
    /// back-pressure, and both are only safe on a fragment boundary. Handing out arbitrary slices
    /// made a late joiner's first append start mid-box, which the decoder rejects outright.</summary>
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

    // The audio counterpart of VideoStreamLine, from the same stream-summary block, e.g.
    // "Stream #0:1: Audio: pcm_alaw, 8000 Hz, mono, s16, 64 kb/s" or
    // "Stream #0:1(und): Audio: aac (LC), 16000 Hz, mono, fltp, 32 kb/s".
    //
    // Codec and sample rate are matched by two separate patterns rather than one combined one on
    // purpose: not every ffmpeg build/codec prints a rate on this line (some report only the codec
    // and channel layout), and a single regex requiring both would then match nothing at all and
    // report no audio whatsoever — the same failure mode ProgressLine's own comment above documents
    // having hit for real when it required a bitrate figure that this pipeline never prints.
    private static readonly Regex AudioStreamLine = new(
        @"Stream #\d+:\d+.*?Audio:\s*([A-Za-z0-9_]+)", RegexOptions.Compiled);

    // Anchored to "Hz" so it can't pick up the bit rate ("64 kb/s") or a channel count.
    private static readonly Regex AudioSampleRate = new(
        @"(\d{3,6})\s*Hz", RegexOptions.Compiled);

    /// <summary>Pure parsing, same reasoning as TryParseVideoStreamLine. Returns null for a line that
    /// isn't an audio stream summary at all; SampleRateHz is null when the line carries no "N Hz"
    /// figure.</summary>
    internal static StreamAudioInfo? TryParseAudioStreamLine(string line)
    {
        var match = AudioStreamLine.Match(line);
        if (!match.Success) return null;
        var rateMatch = AudioSampleRate.Match(line);
        int? sampleRateHz = rateMatch.Success && int.TryParse(rateMatch.Groups[1].Value, out var hz) ? hz : null;
        return new StreamAudioInfo(match.Groups[1].Value, sampleRateHz);
    }

    // ffmpeg's periodic progress line, printed to stderr throughout the run (copy or transcode
    // alike), e.g. "frame= 1234 fps= 15 q=-1.0 size=   10240kB time=00:01:23.45 bitrate=1023.4kbits/s
    // speed=1.00x". Terminated with '\r' (overwrites the same terminal line), not '\n' — .NET's
    // ReadLineAsync already treats a bare '\r' as a line terminator too, so this arrives through
    // DrainStderrAsync's normal per-line loop with no special handling needed. fps can be fractional
    // ("fps= 14.9"); truncated to int for CameraStream's existing int? column.
    //
    // bitrate is captured too but is *always* "N/A" for this app's own pipeline, confirmed against a
    // real captured run of this exact tee spec (BuildTeeOutputs): the tee pseudo-muxer fans one input
    // out to two diverging outputs and has no single meaningful "overall bitrate" to report, so it
    // never prints a number here — not a startup-only transient the way an ordinary single-output mux
    // briefly shows N/A before its first few frames land. Requiring bitrate to be a real number here
    // (as this regex originally did) meant the *whole line* never matched under this pipeline, which
    // silently starved CurrentFps too. bitrate is therefore Optional in the returned tuple — real
    // bitrate is instead derived from completed segment size/duration, see PollForCompletedSegments.
    private static readonly Regex ProgressLine = new(
        @"frame=.*?fps=\s*([\d.]+).*?bitrate=\s*(?:([\d.]+)kbits/s|N/A)", RegexOptions.Compiled);

    /// <summary>Pure parsing, same reasoning as TryParseVideoStreamLine. Fps is truncated to a whole
    /// number; BitrateKbps is null whenever the line reports "bitrate=N/A" (see ProgressLine's own
    /// comment for why that's the normal case for this app, not a rare one). Returns null for a line
    /// that isn't a progress line at all.</summary>
    internal static (int Fps, int? BitrateKbps)? TryParseProgressLine(string line)
    {
        var match = ProgressLine.Match(line);
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, out var fps)) return null;
        int? bitrateKbps = match.Groups[2].Success && double.TryParse(match.Groups[2].Value, out var bitrate) ? (int)bitrate : null;
        return ((int)fps, bitrateKbps);
    }

    /// <summary>Derives a real bitrate from a completed segment's actual byte count over its actual
    /// duration — the source of truth for `-c copy` recording, where the written bytes bit-for-bit
    /// *are* the camera's own encoder output. Used instead of ffmpeg's own progress-line bitrate
    /// figure, which TryParseProgressLine's own comment explains is never available under this app's
    /// tee pipeline. Null for a degenerate (zero/negative) duration rather than dividing by zero.</summary>
    internal static int? EstimateBitrateKbps(long sizeBytes, TimeSpan duration)
        => duration.TotalSeconds > 0 ? (int)(sizeBytes * 8.0 / 1000.0 / duration.TotalSeconds) : null;

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

        // Same whole-session scope as reportedPaths, for the same reason: an ffmpeg reconnect mid-way
        // through must not lose track of a still-open file's already-observed first-real-data instant.
        // See PollForCompletedSegments' own comment for what this is for.
        var firstRealDataObservedUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested)
        {
            State = StreamRecordingState.Connecting;
            Process? process = null;

            try
            {
                process = StartFfmpeg();
                var stderrTask = DrainStderrAsync(process, ct);
                // A new process means fresh throughput stats too — carrying the previous attempt's
                // fps/bitrate forward would show a "live" number for a stream that's actually
                // reconnecting.
                CurrentFps = null;
                CurrentBitrateKbps = null;
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

                    PollForCompletedSegments(reportedPaths, firstRealDataObservedUtc);

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

                // Kills whenever the inner loop ended with the process still alive — not just the
                // stalled case. Previously this only killed on a stall, so a plain cancellation (ct
                // fires while the process is healthy) fell straight through to WaitForExitAsync below
                // with nothing having ever told ffmpeg to stop — a real, confirmed hang risk once M18
                // added the first caller that cancels a *single* camera's CTS mid-run to restart it
                // for a config change (previously only whole-node shutdown ever cancelled this, which
                // cancels and awaits every camera's session together — the same hang, just harder to
                // notice with everything blocked on it at once rather than one camera visibly stuck).
                // A process that already exited on its own (!process.HasExited is false here) is
                // correctly left alone either way.
                if (!process.HasExited)
                {
                    TryKill(process);
                }

                // Bounded — see RecordingSessionOptions.KillTimeoutSeconds' own doc comment for why
                // an unbounded wait here was a real, confirmed multi-hour outage: a killed process
                // stuck on unresponsive network I/O (this fleet's SMB-backed storage dropping
                // mid-write) can take far longer than this to actually exit, and until it does, this
                // camera's *entire* recording session sat frozen at this exact line — never reaching
                // the reconnect logic below, never starting a fresh attempt. When the timeout wins the
                // race, the old process (and its now-orphaned stdout/stderr drain tasks) is simply
                // abandoned rather than blocked on any further — it isn't leaked, just left to finish
                // exiting on its own time; the loop moves on to a fresh attempt regardless.
                var exitAndDrain = Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), stderrTask, stdoutTask);
                var exitedCleanly = await Task.WhenAny(exitAndDrain, Task.Delay(TimeSpan.FromSeconds(options.KillTimeoutSeconds))) == exitAndDrain;
                if (!exitedCleanly)
                {
                    logger.LogWarning(
                        "ffmpeg did not exit within {Timeout}s of being killed — abandoning this attempt and starting a fresh one rather than blocking recording indefinitely. " +
                        "The old process may still be alive briefly (stuck on an unresponsive network write) and will exit on its own once that clears.",
                        options.KillTimeoutSeconds);
                    // Observed in the background so a fault here (e.g. reading a stream this
                    // attempt's own Dispose() below has since torn down) can never surface as an
                    // unobserved-task-exception, and so the eventual outcome is at least visible in
                    // the log rather than vanishing silently.
                    _ = exitAndDrain.ContinueWith(t =>
                    {
                        if (t.IsFaulted) logger.LogDebug(t.Exception, "Abandoned ffmpeg attempt's own cleanup finished with an error (harmless — already moved on).");
                        else logger.LogDebug("Abandoned ffmpeg attempt finally finished exiting on its own.");
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                // Safe either way — purely reads whatever's actually on disk right now, doesn't
                // depend on the process itself having exited.
                FinalizeInProgressSegment(reportedPaths, firstRealDataObservedUtc);

                if (ct.IsCancellationRequested) break;

                if (exitedCleanly)
                {
                    if (!stalled && process.ExitCode == 0)
                    {
                        // Clean exit with code 0 shouldn't normally happen for a live RTSP tee (only
                        // stops on kill/cancel) — treat it as a failure needing backoff rather than a
                        // silent stop, so a camera that closes the connection doesn't quietly stop
                        // recording forever.
                        logger.LogWarning("ffmpeg exited cleanly (code 0) without being asked to — restarting.");
                    }
                    LastError = $"ffmpeg exited with code {process.ExitCode}.";
                }
                else
                {
                    LastError = $"ffmpeg did not exit within {options.KillTimeoutSeconds}s of being killed — abandoned and restarting.";
                }
                consecutiveFailures++;
                TotalReconnectCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                consecutiveFailures++;
                TotalReconnectCount++;
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

        var args = new List<string> { "-nostdin", "-rtsp_transport", "tcp", "-timeout", "5000000" };
        args.AddRange(BuildDecodeArgs(options));
        args.Add("-i");
        args.Add(options.RtspUri);
        args.AddRange(BuildCodecArgs(options));
        args.AddRange(["-map", "0:v", "-map", "0:a?", "-f", "tee", "-y", teeOutputs]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        logger.LogInformation("Starting ffmpeg for {RtspUri} -> {OutputDirectory}", CredentialScrubber.Scrub(options.RtspUri), options.OutputDirectory);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        return process;
    }

    /// <summary>How many bytes a still-open segment file must reach before it's trusted to contain a
    /// real frame rather than just the `frag_keyframe+empty_moov` header ffmpeg writes the instant it
    /// opens the file (StartFfmpeg's own comment). Comfortably larger than any bare fMP4 ftyp+moov
    /// header, comfortably smaller than even a highly-compressed single keyframe at realistic camera
    /// bitrates — see PollForCompletedSegments' own comment for why this matters.</summary>
    private const long MinRealDataBytes = 4096;

    /// <summary>internal, not private: unit-tested directly against a real temp directory (see
    /// LarisVMS.Tests) to prove a pre-seeded reportedPaths entry is skipped rather than re-firing
    /// SegmentCompleted — the actual bug fix in RunAsync's knownPaths parameter, exercised here
    /// without needing to spawn a real ffmpeg process.
    ///
    /// firstRealDataObservedUtc is RunAsync's own dictionary, threaded through the same way
    /// reportedPaths is — see its declaration there for why this is scoped to the whole session
    /// rather than reset per ffmpeg attempt.</summary>
    internal void PollForCompletedSegments(HashSet<string> reportedPaths, Dictionary<string, DateTime> firstRealDataObservedUtc)
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

        // The still-open (last) file gets a chance to record its own true start here, on whichever
        // poll first catches it past the header-only size — before it's ever looked at by the
        // completed-files loop below, possibly several polls later once a newer file supersedes it.
        // A completed segment's own FileInfo.CreationTimeUtc is the moment ffmpeg opened/truncated the
        // file, which can be well before the first real frame lands (a full RTSP handshake/negotiation
        // happens in between on the first segment after any (re)connect) — every timeline position
        // computed downstream from that timestamp then reads as earlier than the true content by
        // exactly that connect gap. This bounds the error to roughly one poll interval instead.
        if (files.Length > 0)
        {
            var openPath = files[^1];
            if (!firstRealDataObservedUtc.ContainsKey(openPath))
            {
                FileInfo? openInfo;
                try { openInfo = new FileInfo(openPath); }
                catch (IOException) { openInfo = null; }
                if (openInfo is { Length: >= MinRealDataBytes }) firstRealDataObservedUtc[openPath] = DateTime.UtcNow;
            }
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

            // Segment i's end is really "whenever segment i+1 truly started" — the exact same instant
            // ResolveSegmentBoundary will be asked for again once files[i+1] itself reaches this loop
            // in a later iteration, as *its own* start. See that method's own doc comment for why
            // routing both through the same write-through resolver (not two independent lookups that
            // can each fall back differently) is what makes end(N) and start(N+1) provably equal.
            var start = ResolveSegmentBoundary(path, firstRealDataObservedUtc);
            var end = ResolveSegmentBoundary(files[i + 1], firstRealDataObservedUtc);
            firstRealDataObservedUtc.Remove(path);
            LastSegmentAt = DateTime.UtcNow;
            if (EstimateBitrateKbps(info.Length, end - start) is { } bitrateKbps) CurrentBitrateKbps = bitrateKbps;
            SegmentCompleted?.Invoke(new RecordingSegment(path, start, end, info.Length));
        }
    }

    /// <summary>Resolves one segment file's boundary instant — preferring the true first-real-data
    /// moment recorded elsewhere in <paramref name="firstRealDataObservedUtc"/> (see
    /// PollForCompletedSegments' own "check the still-open file" block), falling back to
    /// <see cref="FileInfo.CreationTimeUtc"/> only when this file was never caught mid-growth.
    ///
    /// The fallback is written BACK into the dictionary, not just returned — this is what makes
    /// end(segment N) and start(segment N+1) provably the same value even though they're computed on
    /// two different calls, possibly a whole poll cycle apart. Without the write-through, a fallback
    /// computed here (asking "when did files[i+1] truly start?", to close out segment i) and that
    /// same file's own start (asked again once files[i+1] reaches its own turn in the loop, by which
    /// point its real-data check may have finally succeeded) could each independently land on a
    /// different value — confirmed live as a false "gap" between adjacent Segments rows on roughly
    /// 41% of all transitions in one night's recording, sized anywhere from a couple of seconds to
    /// nearly a minute, even though the underlying video file was continuous throughout. Playback
    /// then correctly (from its own data) reported "no recording available" for a window a real,
    /// intact video segment actually covered.</summary>
    private static DateTime ResolveSegmentBoundary(string filePath, Dictionary<string, DateTime> firstRealDataObservedUtc)
    {
        if (firstRealDataObservedUtc.TryGetValue(filePath, out var observed)) return observed;
        var fallback = new FileInfo(filePath).CreationTimeUtc;
        firstRealDataObservedUtc[filePath] = fallback;
        return fallback;
    }

    /// <summary>Called after the process has exited (clean stop, crash, or watchdog kill) — the
    /// last file ffmpeg was writing when it stopped is still a real, playable segment (frag_keyframe
    /// + empty_moov survives a truncated write), so it is reported too rather than silently dropped.</summary>
    private void FinalizeInProgressSegment(HashSet<string> reportedPaths, Dictionary<string, DateTime> firstRealDataObservedUtc)
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

        var start = firstRealDataObservedUtc.TryGetValue(last, out var observed) ? observed : info.CreationTimeUtc;
        var end = DateTime.UtcNow;
        if (EstimateBitrateKbps(info.Length, end - start) is { } bitrateKbps) CurrentBitrateKbps = bitrateKbps;
        SegmentCompleted?.Invoke(new RecordingSegment(last, start, end, info.Length));
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        var resolutionReported = false;
        var audioReported = false;
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
                logger.LogDebug("ffmpeg: {Line}", CredentialScrubber.Scrub(line));

                if (!resolutionReported && TryParseVideoStreamLine(line) is { } resolution)
                {
                    resolutionReported = true;
                    StreamResolutionDetected?.Invoke(resolution);
                }

                if (!audioReported && TryParseAudioStreamLine(line) is { } audio)
                {
                    audioReported = true;
                    StreamAudioDetected?.Invoke(audio);
                }

                if (TryParseProgressLine(line) is { } progress)
                {
                    CurrentFps = progress.Fps;
                    // Only overwrites when the line actually carried a number — under this app's own
                    // tee pipeline it never does (see ProgressLine's comment), so this leaves whatever
                    // PollForCompletedSegments most recently derived from real segment bytes alone
                    // instead of stomping it back to null on every single progress tick.
                    if (progress.BitrateKbps is { } bitrate) CurrentBitrateKbps = bitrate;
                }

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("could not", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("unable to", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("ffmpeg: {Line}", CredentialScrubber.Scrub(line));
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

        // A fragment that never completes means the stream is malformed (or isn't fMP4 at all).
        // Bounded so that can't grow without limit; generously above any real fragment. The pipe leg
        // is muxed with frag_duration (BuildTeeOutputs / LiveFragDurationMicros) so a fragment is now
        // ~500ms of video, well under one GOP — this limit stays as the malformed-stream backstop.
        const int MaxFragmentBytes = 32 * 1024 * 1024;

        // Read through a PipeReader rather than Stream.ReadAsync into an array of our own. The pipe
        // owns pooled buffers and hands them over as a ReadOnlySequence, and AdvanceTo(consumed,
        // examined) expresses exactly the problem here: "I have looked at all of this, but could only
        // consume up to the end of the last whole fragment — keep the rest and call me when there's
        // more." That is the buffering, growth and compaction this used to do by hand, and doing it
        // by hand meant copying every byte ffmpeg produced into a second buffer first. Now nothing is
        // copied at all unless a fragment is actually being handed to a viewer.
        var reader = PipeReader.Create(process.StandardOutput.BaseStream,
            // Leave the underlying stream alone on completion — the process teardown path owns it.
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
                            logger.LogWarning("Live init segment exceeded {MaxBytes} bytes without a complete moov box — giving up on live view for this attempt.", MaxInitSegmentBytes);
                            initSegmentResolved = true; // stop trying; recording itself is unaffected
                            reader.AdvanceTo(buffer.End);
                            continue;
                        }
                        // Nothing consumable yet, but everything has been examined — this is what
                        // tells the pipe to hold what it has and wait for more rather than spin.
                        reader.AdvanceTo(buffer.Start, buffer.End);
                        if (result.IsCompleted) break;
                        continue;
                    }

                    initSegmentResolved = true;
                    // The one unavoidable copy on this path: the init segment is cached for the whole
                    // life of the session and handed to every future viewer, so it has to outlive the
                    // pipe's buffer.
                    LiveInitSegment = buffer.Slice(0, initEnd.Value).ToArray();
                    _initSegmentTcs.TrySetResult(LiveInitSegment);
                    consumed = buffer.GetPosition(initEnd.Value);
                }

                // Everything from here is fragments. Anything past the init segment in this same read
                // is already the start of the first one, so it falls straight through.
                while (true)
                {
                    var remaining = buffer.Slice(consumed);
                    var length = Mp4BoxScanner.TryFindFragmentEnd(remaining);
                    if (length is null)
                    {
                        if (remaining.Length > MaxFragmentBytes)
                        {
                            logger.LogWarning("Live fragment exceeded {MaxBytes} bytes without a complete moof+mdat — " +
                                "dropping the buffer; live view will resync on the next clean fragment.", MaxFragmentBytes);
                            consumed = buffer.End;
                        }
                        break;
                    }

                    // Read once into a local: a viewer disconnecting between the null check and the
                    // invoke would otherwise throw, and re-reading the field could also skip a
                    // fragment (non-null here, null by the time it's used). This is also what makes
                    // the no-viewer path free — with nobody subscribed the fragment is stepped over
                    // without ever being copied, which on a 24/7 recorder is the common case by far.
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
            logger.LogDebug(ex, "stdout (live) drain ended.");
        }
        finally
        {
            await reader.CompleteAsync();
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
    /// <summary>M18: deliberately always empty — confirmed live (real Intel/NVIDIA hardware) that
    /// pairing decode-side `-hwaccel` with the plain CPU `drawbox` filter this pipeline uses makes
    /// ffmpeg fail immediately on every attempt (RecordingSession never reaches Recording, cycling
    /// Connecting -&gt; Backoff forever with nothing ever produced). Hardware-decoded frames stay on
    /// the GPU in a format `drawbox` — an ordinary software filter with no hwaccel awareness — can't
    /// operate on, and this pipeline doesn't set up the explicit `hwdownload`/`format` conversion a
    /// hwaccel decode would need to feed a CPU filter safely. The encoder (BuildCodecArgs) is still
    /// hardware when one was detected — only the decode step falls back to software here, which is
    /// the cheaper half of the work anyway. EncodePipeline.DecodeHwaccelArgs itself is untouched and
    /// still available for a future consumer whose filter chain is itself hardware-native (e.g. an
    /// adaptive-streaming downscale using `scale_cuda`/`vpp_qsv` instead of a CPU `scale` filter),
    /// where pairing it with hwaccel decode would actually be safe.</summary>
    internal static IReadOnlyList<string> BuildDecodeArgs(RecordingSessionOptions options) => [];

    /// <summary>M18: `-c copy` (this pipeline's default, zero-CPU-cost path) for a camera with no
    /// enabled Privacy zone, or a real filter+encode for one that has at least one — see
    /// PrivacyMaskFilterBuilder for how a zone becomes a filter expression, and
    /// EncoderSelection.ChooseH264Encoder for how NodeWorker picks options.VideoEncoder. Audio is
    /// always `-c:a copy` on the transcode path — masking is a video-only concern, nothing about a
    /// camera's audio track needs to change because one of its Privacy zones changed.</summary>
    internal static IReadOnlyList<string> BuildCodecArgs(RecordingSessionOptions options)
    {
        if (options.PrivacyMaskFilters is not { Count: > 0 } filters) return ["-c", "copy"];

        var encoder = options.VideoEncoder ?? "libx264";
        var encodeArgs = EncodePipeline.BuildArgs(new EncodePipelineOptions(
            encoder, VideoFilters: filters, Preset: EncodePipeline.RealtimePreset(encoder)));
        return [.. encodeArgs, "-c:a", "copy"];
    }

    internal static string BuildTeeOutputs(int segmentSeconds, string outputPattern) => string.Join('|',
        $"[f=segment:segment_time={segmentSeconds}:segment_atclocktime=1:reset_timestamps=1:strftime=1:" +
        $"segment_format=mp4:segment_format_options=movflags={MseMovFlags}]{EscapeForTee(outputPattern)}",
        $"[f=mp4:movflags={MseMovFlags}:frag_duration={LiveFragDurationMicros}:" +
        $"min_frag_duration={LiveMinFragDurationMicros}]pipe:1");

    /// <summary>The mov muxer flags every leg must use. Both legs are consumed by MSE (live view
    /// reads the pipe, playback reads the recorded files), and MSE requires default_base_moof on
    /// both — see the commentary in StartFfmpeg for what happens when a leg is missing it.</summary>
    private const string MseMovFlags = "+frag_keyframe+empty_moov+default_base_moof";

    /// <summary>Live pipe leg only: flush an fMP4 fragment at least this often (microseconds) even
    /// mid-GOP, instead of only at each camera keyframe. Without it, a camera with a 2-4s keyframe
    /// interval delivers one big fragment every 2-4s, so the browser's SourceBuffer.buffered.end()
    /// lurches forward in GOP-sized steps while currentTime advances smoothly — live-view.js read
    /// that sawtooth as "drift" and oscillated between a 1.5x catch-up and a starved-buffer stall.
    /// +frag_keyframe stays set in <see cref="MseMovFlags"/> (the muxer then flushes on whichever
    /// comes first), so every keyframe still starts a fresh fragment and a late-joining viewer's
    /// first decodable frame is still at most one GOP away. min_frag_duration keeps camera packet
    /// timing jitter from producing a burst of tiny fragments. The segment/recording leg is
    /// deliberately NOT given these — playback (playback-player.js, Mp4FragmentIndexer) depends on
    /// the recorded files fragmenting on keyframes only.</summary>
    private const int LiveFragDurationMicros = 500_000;
    private const int LiveMinFragDurationMicros = 200_000;

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

}
