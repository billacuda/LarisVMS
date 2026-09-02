using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LarisVMS.Media;

/// <summary>
/// Resolves the ffmpeg executable to run. LarisVMS does not bundle ffmpeg — the operator installs it
/// (<c>winget install ffmpeg --scope machine</c>, or any other means) and this locates it. An
/// explicit configured path (<c>--ffmpeg-path</c> / <c>LARISVMS_FFMPEG_PATH</c> / <c>Vision:FfmpegPath</c>)
/// always wins; otherwise <see cref="TryDiscover"/> looks on <c>PATH</c> and in the WinGet package
/// store, and a bare <c>"ffmpeg"</c> (resolved via <c>PATH</c> by the OS) is the last-resort fallback.
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

        return TryDiscover() ?? "ffmpeg";
    }

    /// <summary>Best-effort location of an ffmpeg install when no explicit path is configured: an
    /// <c>ffmpeg</c> already on <c>PATH</c> wins (the Windows Service account sees the machine
    /// <c>PATH</c>, which a <c>--scope machine</c> winget install writes to), then a WinGet package
    /// install — machine scope (<c>%ProgramFiles%\WinGet\Packages</c>) first, then per-user — picking
    /// the newest version folder. Returns <c>null</c> when nothing turns up.</summary>
    public static string? TryDiscover()
    {
        if (FindOnPath() is { } onPath) return onPath;

        foreach (var root in WingetPackageRoots())
        {
            if (NewestFfmpegUnder(root) is { } hit) return hit;
        }

        return null;
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

    private static string? FindOnPath()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry — skip it */ }
        }
        return null;
    }

    private static IEnumerable<string> WingetPackageRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            yield return Path.Combine(programFiles, "WinGet", "Packages");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
    }

    private static string? NewestFfmpegUnder(string packagesRoot)
    {
        if (!Directory.Exists(packagesRoot)) return null;
        try
        {
            return Directory.EnumerateDirectories(packagesRoot, "*FFmpeg*")
                .SelectMany(d => SafeEnumerateFiles(d, "ffmpeg.exe"))
                .OrderByDescending(f => ExtractVersion(f) ?? new Version(0, 0))
                .ThenByDescending(SafeWriteTime)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories); }
        catch { return []; }
    }

    // WinGet lays ffmpeg down under a version-bearing folder, e.g.
    // "...\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe".
    private static readonly Regex VersionInPath = new(@"ffmpeg-(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase);

    private static Version? ExtractVersion(string path)
    {
        var m = VersionInPath.Match(path);
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }
}
