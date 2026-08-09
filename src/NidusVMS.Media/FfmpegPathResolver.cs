using System.Diagnostics;

namespace NidusVMS.Media;

/// <summary>
/// Resolves the ffmpeg executable to run. A node deployment bundles its own ffmpeg binaries (see
/// the plan's build-node.ps1 section — not yet implemented), but for development and for a node
/// installed onto a machine that already has ffmpeg on PATH, an explicit configured path takes
/// priority and a bare "ffmpeg" (resolved via PATH by the OS) is the fallback.
/// </summary>
public static class FfmpegPathResolver
{
    public static string Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (!File.Exists(configuredPath))
                throw new FileNotFoundException($"Configured ffmpeg path does not exist: {configuredPath}");
            return configuredPath;
        }
        return "ffmpeg";
    }

    /// <summary>Best-effort check that the resolved path actually launches — distinguishes "ffmpeg
    /// isn't installed/on PATH" from a stream-specific failure, which fail very differently
    /// (immediately vs. after a real connection attempt) and deserve different log messages.</summary>
    public static async Task<bool> IsRunnableAsync(string ffmpegPath, CancellationToken ct = default)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            if (process is null) return false;
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
