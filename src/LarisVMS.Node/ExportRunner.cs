using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Media;

namespace LarisVMS.Node;

/// <summary>
/// Runs one export item's ffmpeg concat on this node: writes an ffmpeg concat-demuxer list file
/// under {storageRoot}/exports/, invokes ffmpeg (-f concat -c copy — a pure remux, no transcode,
/// since one camera's own segments already share codec/resolution by construction), and reports
/// success/failure back to the web tier. Constructed as a singleton alongside NodeWorker/
/// StorageManager (see Program.cs) but does no polling of its own — POST /export/{cameraId} kicks
/// off one run per request via Task.Run, after already responding 202.
///
/// ProcessStartInfo/stderr-draining shape mirrors RecordingSession.StartFfmpeg exactly (redirected
/// std streams, CreateNoWindow, arguments built token-by-token via ArgumentList, not a single
/// command-line string) — the only real difference is this run starts, finishes, and exits once,
/// rather than supervising a long-lived RTSP capture.
/// </summary>
public class ExportRunner(NodeApiClient api, string ffmpegPath, ILogger<ExportRunner> logger, FootageStore? footage = null)
{
    /// <summary>A recorded two-way audio press, placed OffsetSeconds into the export (negative when
    /// it began before the first segment).</summary>
    public sealed record TalkClip(string FilePath, double OffsetSeconds);

    public async Task RunAsync(Guid exportItemId, Guid cameraId, List<string> segmentFilePaths,
        string storageRoot, string outputFileName, CancellationToken ct,
        IReadOnlyList<TalkClip>? talkClips = null, bool segmentsHaveAudio = false,
        IReadOnlyList<string>? redactionFilters = null, string? videoEncoder = null,
        IReadOnlyList<string?>? expectedSha256 = null, bool convertToH264 = false, string? h264Encoder = null)
    {
        var exportsDir = Path.Combine(storageRoot, "exports");
        var listFilePath = Path.Combine(exportsDir, $"{exportItemId:N}.concat.txt");
        var outputPath = Path.Combine(exportsDir, outputFileName);

        try { await Slots.WaitAsync(ct); }
        catch (OperationCanceledException) { return; } // the service is stopping: see below

        try
        {
            Directory.CreateDirectory(exportsDir);
            // Tamper evidence: every source file re-hashed and compared with the hash recorded when it
            // was written, for the export manifest. A mismatch doesn't stop the export — the manifest
            // says which segments changed, which is the point.
            var checks = new List<ExportSegmentCheck>(segmentFilePaths.Count);
            for (var i = 0; i < segmentFilePaths.Count; i++)
            {
                var expected = expectedSha256 is not null && i < expectedSha256.Count ? expectedSha256[i] : null;
                string? actual = null;
                // Of the original bytes, whether or not the file is encrypted at rest.
                try { actual = footage is null ? await SegmentChain.HashFileAsync(segmentFilePaths[i], ct) : await footage.HashAsync(segmentFilePaths[i], ct); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { /* reported as unreadable */ }
                checks.Add(new ExportSegmentCheck(segmentFilePaths[i], actual,
                    expected is null ? null : string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)));
            }
            if (checks.Any(c => c.Matches == false))
                logger.LogWarning("Export item {ExportItemId}: {Count} source segment(s) no longer match the hash recorded when they were written.",
                    exportItemId, checks.Count(c => c.Matches == false));

            // Encrypted footage reaches ffmpeg through the node's loopback decrypting endpoint.
            var inputs = new List<string>(segmentFilePaths.Count);
            foreach (var p in segmentFilePaths) inputs.Add(footage is null ? p : await footage.FfmpegInputAsync(p, ct));
            var clips = new List<TalkClip>();
            foreach (var clip in talkClips ?? [])
                clips.Add(footage is null ? clip : clip with { FilePath = await footage.FfmpegInputAsync(clip.FilePath, ct) });
            talkClips = clips;
            var loopback = inputs.Any(FootageStore.IsLoopbackInput);

            await File.WriteAllTextAsync(listFilePath, BuildConcatList(inputs), ct);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                // Same "explicitly redirected, never written to" reasoning as RecordingSession —
                // launched under a service with no real controlling terminal.
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (var a in BuildArgs(listFilePath, outputPath, talkClips, segmentsHaveAudio, redactionFilters, videoEncoder, loopback)) psi.ArgumentList.Add(a);

            logger.LogInformation("Starting export ffmpeg for item {ExportItemId} ({SegmentCount} segment(s), {TalkCount} talk clip(s)) -> {OutputPath}",
                exportItemId, segmentFilePaths.Count, talkClips?.Count ?? 0, outputPath);

            var exitCode = await RunFfmpegAsync(psi, ct);
            if (exitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException($"ffmpeg exited with code {exitCode}.");
            }

            var size = new FileInfo(outputPath).Length;
            var outputSha256 = await SegmentChain.HashFileAsync(outputPath, ct);
            var converted = convertToH264 ? await ConvertAsync(exportItemId, outputPath, h264Encoder, ct) : null;
            await api.ReportExportCompleteAsync([new ExportCompleteReportItem(exportItemId, true, outputPath, size, null, outputSha256, checks,
                converted?.Path, converted?.Size, converted?.Sha256)], ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The server marks it failed when this node reports a new uptime.
            logger.LogWarning("Export item {ExportItemId} was cut off by the service stopping.", exportItemId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export item {ExportItemId} failed.", exportItemId);
            try
            {
                await api.ReportExportCompleteAsync([new ExportCompleteReportItem(exportItemId, false, null, null, ex.Message)], CancellationToken.None);
            }
            catch (Exception reportEx)
            {
                logger.LogError(reportEx, "Also failed to report export item {ExportItemId}'s failure back to the web tier.", exportItemId);
            }
        }
        finally
        {
            Slots.Release();
            try { File.Delete(listFilePath); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

    /// <summary>The H.264 copy of a finished export for the standalone player, beside it as
    /// {name}.h264.mp4. A copy that can't be made is logged and left out; the export itself is fine
    /// without it (the player falls back to the original).</summary>
    private async Task<(string Path, long Size, string Sha256)?> ConvertAsync(Guid exportItemId, string outputPath, string? encoder, CancellationToken ct)
    {
        var convertedPath = ConvertedPath(outputPath);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };
            foreach (var a in BuildConvertArgs(outputPath, convertedPath, encoder)) psi.ArgumentList.Add(a);
            logger.LogInformation("Converting export item {ExportItemId} to H.264 for the standalone player -> {Path}", exportItemId, convertedPath);
            var exitCode = await RunFfmpegAsync(psi, ct);
            if (exitCode != 0 || !File.Exists(convertedPath))
                throw new InvalidOperationException($"ffmpeg exited with code {exitCode}.");
            return (convertedPath, new FileInfo(convertedPath).Length, await SegmentChain.HashFileAsync(convertedPath, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Export item {ExportItemId}: the H.264 copy for the standalone player couldn't be made; the export is kept without it.", exportItemId);
            try { File.Delete(convertedPath); } catch (IOException) { /* best effort */ }
            return null;
        }
    }

    /// <summary>"x.mp4" -> "x.h264.mp4", in the same folder.</summary>
    internal static string ConvertedPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath) ?? "", Path.GetFileNameWithoutExtension(outputPath) + ".h264.mp4");

    /// <summary>Re-encodes the video to H.264 (the audio is copied), with the index at the front so a
    /// browser can start playing and seek straight away.</summary>
    internal static List<string> BuildConvertArgs(string inputPath, string outputPath, string? encoder) =>
    [
        "-nostdin", "-i", inputPath, "-map", "0:v", "-map", "0:a?",
        .. EncodePipeline.BuildArgs(new EncodePipelineOptions(encoder ?? "libx264", Preset: EncodePipeline.RealtimePreset(encoder ?? "libx264"))),
        "-c:a", "copy", "-movflags", "+faststart", "-y", outputPath,
    ];

    /// <summary>The ffmpeg command for one export. With no talk clips: a pure remux (-c copy). With
    /// some: video is still copied, and the audio is the camera's own track (or silence, when it has
    /// none) with each recorded press mixed in at its offset, encoded to AAC — so an exported clip
    /// carries both directions of a two-way audio conversation, the way it sounded live.
    ///
    /// Everything is brought to 48 kHz mono before mixing (amix needs matching formats; the talk
    /// WAVs are 8 kHz mono). normalize=0 keeps each input at full level instead of amix's default of
    /// dividing every input's volume by the input count. The mix runs as long as its first input — the
    /// camera's audio, or endless silence — and -shortest ends the file with the video.</summary>
    internal static List<string> BuildArgs(string listFilePath, string outputPath, IReadOnlyList<TalkClip> talkClips, bool segmentsHaveAudio,
        IReadOnlyList<string>? redactionFilters = null, string? videoEncoder = null, bool loopbackInputs = false)
    {
        // The concat demuxer only opens local files unless told otherwise; encrypted segments are
        // listed as loopback http URLs (FootageStore).
        var args = new List<string> { "-nostdin" };
        if (loopbackInputs) args.AddRange(["-protocol_whitelist", "file,http,tcp"]);
        args.AddRange(["-f", "concat", "-safe", "0", "-i", listFilePath]);
        var redact = redactionFilters is { Count: > 0 };

        // Redacted areas need the video decoded, blacked out and re-encoded; otherwise it's copied.
        List<string> VideoEncode(bool viaGraph) =>
        [
            .. (viaGraph ? [] : new[] { "-vf", string.Join(',', redactionFilters!) }),
            .. EncodePipeline.BuildArgs(new EncodePipelineOptions(videoEncoder ?? "libx264",
                Preset: EncodePipeline.RealtimePreset(videoEncoder ?? "libx264"))),
        ];

        if (talkClips.Count == 0)
        {
            if (!redact)
            {
                args.AddRange(["-c", "copy", "-y", outputPath]);
                return args;
            }
            args.AddRange(["-map", "0:v", "-map", "0:a?"]);
            args.AddRange(VideoEncode(viaGraph: false));
            args.AddRange(["-c:a", "copy", "-y", outputPath]);
            return args;
        }

        foreach (var clip in talkClips) args.AddRange(["-i", clip.FilePath]);

        const string Format = "aresample=48000,aformat=sample_fmts=fltp:channel_layouts=mono";
        var filter = new StringBuilder();
        filter.Append(segmentsHaveAudio ? $"[0:a]{Format}[base];" : $"anullsrc=r=48000:cl=mono,{Format}[base];");
        var labels = new StringBuilder("[base]");
        for (var i = 0; i < talkClips.Count; i++)
        {
            var offset = talkClips[i].OffsetSeconds;
            filter.Append($"[{i + 1}:a]{Format}");
            filter.Append(offset >= 0
                ? FormattableString.Invariant($",adelay={Math.Round(offset * 1000)}:all=1")
                : FormattableString.Invariant($",atrim=start={-offset:0.###},asetpts=PTS-STARTPTS"));
            filter.Append($"[t{i}];");
            labels.Append($"[t{i}]");
        }
        filter.Append($"{labels}amix=inputs={talkClips.Count + 1}:duration=first:dropout_transition=0:normalize=0[aout]");
        // With a complex graph in use, the redaction joins it rather than going through -vf.
        if (redact) filter.Append($";[0:v]{string.Join(',', redactionFilters!)}[vout]");

        args.AddRange(["-filter_complex", filter.ToString(), "-map", redact ? "[vout]" : "0:v", "-map", "[aout]"]);
        args.AddRange(redact ? VideoEncode(viaGraph: true) : ["-c:v", "copy"]);
        args.AddRange(["-c:a", "aac", "-b:a", "96k", "-shortest", "-y", outputPath]);
        return args;
    }

    /// <summary>An export's redaction areas (ExportJobItem.RedactionJson, a JSON array of zone-format
    /// polygons) as black-box filters — the same exact-shape mask as Privacy zones. Empty when there
    /// are none. Null when areas were asked for but can't be read: the export must then fail rather
    /// than go out unredacted.</summary>
    internal static IReadOnlyList<string>? RedactionFilters(string? redactionJson)
    {
        if (string.IsNullOrWhiteSpace(redactionJson)) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(redactionJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            if (doc.RootElement.GetArrayLength() == 0) return [];
            var polygons = doc.RootElement.EnumerateArray().Select(p => ZoneRasterizer.ParsePolygon(p.GetRawText())).ToList();
            // Every area has to be a real shape; one that reads as nothing would leave its part showing.
            if (polygons.Any(p => p.Count < 3)) return null;
            var filters = PrivacyMaskFilterBuilder.BuildMaskFilters(polygons);
            return filters.Count > 0 ? filters : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>ffmpeg concat demuxer format: one `file '&lt;path&gt;'` line per segment. Per ffmpeg's
    /// own documented concat-file quoting rule, the only character that needs escaping inside the
    /// single-quoted form is a literal single quote itself — close the quote, escape one, reopen
    /// (<c>'\''</c>) — everything else, including a Windows path's backslashes, is taken literally.
    /// internal, not private: unit-tested directly against the escaping rule without spawning
    /// ffmpeg.</summary>
    internal static string BuildConcatList(IEnumerable<string> segmentFilePaths)
    {
        var sb = new StringBuilder();
        foreach (var path in segmentFilePaths)
        {
            sb.Append("file '").Append(path.Replace("'", "'\\''")).Append("'\n");
        }
        return sb.ToString();
    }

    // Reading, hashing, and perhaps re-encoding hours of footage, on the machine that's recording.
    private static readonly SemaphoreSlim Slots = new(2, 2);

    /// <summary>Runs ffmpeg to its end; kills it when the service stops, which would otherwise leave it
    /// running (and writing) without it.</summary>
    private async Task<int> RunFfmpegAsync(ProcessStartInfo psi, CancellationToken ct)
    {
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        var stderrTask = DrainStderrAsync(process, ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not stop the export's ffmpeg (it may have just exited)."); }
            throw;
        }
        await stderrTask;
        return process.ExitCode;
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct)) is not null)
            {
                logger.LogDebug("export ffmpeg: {Line}", line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "export ffmpeg stderr drain ended.");
        }
    }
}
