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
        IReadOnlyList<string?>? expectedSha256 = null)
    {
        var exportsDir = Path.Combine(storageRoot, "exports");
        Directory.CreateDirectory(exportsDir);

        var listFilePath = Path.Combine(exportsDir, $"{exportItemId:N}.concat.txt");
        var outputPath = Path.Combine(exportsDir, outputFileName);

        try
        {
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

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            var stderrTask = DrainStderrAsync(process, ct);
            await process.WaitForExitAsync(ct);
            await stderrTask;

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}.");
            }

            var size = new FileInfo(outputPath).Length;
            var outputSha256 = await SegmentChain.HashFileAsync(outputPath, ct);
            await api.ReportExportCompleteAsync([new ExportCompleteReportItem(exportItemId, true, outputPath, size, null, outputSha256, checks)], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
            try { File.Delete(listFilePath); } catch (IOException) { /* best-effort cleanup */ }
        }
    }

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
    /// are none or the JSON is unreadable.</summary>
    internal static IReadOnlyList<string> RedactionFilters(string? redactionJson)
    {
        if (string.IsNullOrWhiteSpace(redactionJson)) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(redactionJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return [];
            var polygons = doc.RootElement.EnumerateArray().Select(p => ZoneRasterizer.ParsePolygon(p.GetRawText())).ToList();
            return PrivacyMaskFilterBuilder.BuildMaskFilters(polygons);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
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
