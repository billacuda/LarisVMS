using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace FakeOnvif;

/// <summary>JPEG snapshots for GetSnapshotUri. Grabs the frame of the camera's Main clip that's
/// roughly "live" (clip position = time since start, modulo the clip length) and caches it briefly,
/// so repeated requests don't spawn an ffmpeg each time.</summary>
public sealed class SnapshotCache(DemoConfig config, ILogger<SnapshotCache> logger)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly ConcurrentDictionary<string, double> _durations = new();
    private readonly ConcurrentDictionary<string, (DateTime At, byte[] Jpeg)> _cache = new();
    private readonly SemaphoreSlim _gate = new(2);

    private string Ffmpeg => Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";

    public async Task<byte[]?> GetAsync(CameraConfig camera, CancellationToken ct)
    {
        if (_cache.TryGetValue(camera.Id, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Jpeg;

        var clip = Path.Combine(config.ClipsDir, $"{camera.Id}_main.mp4");
        if (!File.Exists(clip)) return null;

        await _gate.WaitAsync(ct);
        try
        {
            var duration = _durations.GetOrAdd(camera.Id, _ => ProbeDuration(clip));
            var offset = duration > 1 ? (DateTime.UtcNow - _startedUtc).TotalSeconds % (duration - 0.5) : 0;
            var jpeg = await RunAsync(Ffmpeg, ["-hide_banner", "-loglevel", "error",
                "-ss", offset.ToString("0.00", CultureInfo.InvariantCulture), "-i", clip,
                "-frames:v", "1", "-q:v", "4", "-f", "image2", "-c:v", "mjpeg", "pipe:1"], ct);
            if (jpeg.Length > 0) _cache[camera.Id] = (DateTime.UtcNow, jpeg);
            return jpeg.Length > 0 ? jpeg : null;
        }
        finally { _gate.Release(); }
    }

    private double ProbeDuration(string clip)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(Ffmpeg) ?? "", "ffprobe");
        if (!Path.IsPathRooted(ffprobe)) ffprobe = "ffprobe";
        var output = RunAsync(ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", clip],
            CancellationToken.None).GetAwaiter().GetResult();
        var text = System.Text.Encoding.ASCII.GetString(output).Trim();
        if (double.TryParse(text, CultureInfo.InvariantCulture, out var seconds)) return seconds;
        logger.LogWarning("Could not read the duration of {Clip}; snapshots will use its first frame.", clip);
        return 0;
    }

    private static async Task<byte[]> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}.");
        using var ms = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var drainErr = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, drainErr, process.WaitForExitAsync(ct));
        return process.ExitCode == 0 ? ms.ToArray() : [];
    }
}
