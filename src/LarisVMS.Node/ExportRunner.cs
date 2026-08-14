using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;

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
public class ExportRunner(NodeApiClient api, string ffmpegPath, ILogger<ExportRunner> logger)
{
    public async Task RunAsync(Guid exportItemId, Guid cameraId, List<string> segmentFilePaths,
        string storageRoot, string outputFileName, CancellationToken ct)
    {
        var exportsDir = Path.Combine(storageRoot, "exports");
        Directory.CreateDirectory(exportsDir);

        var listFilePath = Path.Combine(exportsDir, $"{exportItemId:N}.concat.txt");
        var outputPath = Path.Combine(exportsDir, outputFileName);

        try
        {
            await File.WriteAllTextAsync(listFilePath, BuildConcatList(segmentFilePaths), ct);

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
            string[] args = ["-nostdin", "-f", "concat", "-safe", "0", "-i", listFilePath, "-c", "copy", "-y", outputPath];
            foreach (var a in args) psi.ArgumentList.Add(a);

            logger.LogInformation("Starting export ffmpeg for item {ExportItemId} ({SegmentCount} segment(s)) -> {OutputPath}",
                exportItemId, segmentFilePaths.Count, outputPath);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            var stderrTask = DrainStderrAsync(process, ct);
            await process.WaitForExitAsync(ct);
            await stderrTask;

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}.");
            }

            var size = new FileInfo(outputPath).Length;
            await api.ReportExportCompleteAsync([new ExportCompleteReportItem(exportItemId, true, outputPath, size, null)], ct);
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
