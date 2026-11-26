using System.Diagnostics;

namespace LarisVMS.Media;

/// <summary>
/// M17 foundation: which hardware/software video encoders this node's own ffmpeg build actually
/// offers. Probed once per node process (hardware and the installed ffmpeg build don't change while
/// the process is running) rather than per-camera or per-request — see LarisVMS.Node's NodeWorker,
/// which caches the result and reports it on every heartbeat the same way it already reports Version.
///
/// Nothing in the app *uses* a detected hardware encoder yet — this milestone is deliberately just
/// the probe and the admin-visible result. The privacy-mask/adaptive-streaming/archive-downscale
/// features that will actually pick an encoder from this list are later milestones (M18+); building
/// the probe now means they can consume a real answer instead of also having to invent one.
/// </summary>
public static partial class FfmpegCapabilityProber
{
    /// <summary>The only encoder names this app ever looks for or offers — deliberately a closed
    /// list (not "whatever ffmpeg -encoders happens to print") so a future feature picking an
    /// encoder from Node.DetectedEncodersJson has a known, finite set of values to switch on, the
    /// same reasoning PermissionCatalog gives for being a closed list rather than free text. One
    /// software fallback (libx264/libx265, present in essentially every ffmpeg build) plus the H.264/
    /// H.265 encoder from each of the three hardware vendors the roadmap names, plus VAAPI, which is how
    /// Intel and AMD GPUs encode on Linux.</summary>
    public static readonly IReadOnlyList<string> KnownEncoders =
    [
        "libx264", "libx265",
        "h264_qsv", "hevc_qsv",
        "h264_nvenc", "hevc_nvenc",
        "h264_amf", "hevc_amf",
        "h264_vaapi", "hevc_vaapi"
    ];

    /// <summary>Runs `ffmpeg -encoders` and parses which of <see cref="KnownEncoders"/> it lists.
    /// Never throws for a probe failure (ffmpeg not runnable, times out, unexpected output) — capability
    /// detection failing must not be able to stop the node from starting or recording; it just means
    /// nothing gets reported this heartbeat, same as ThumbnailBackfillService and other best-effort
    /// node-side work in this app.</summary>
    public static async Task<IReadOnlyList<string>> ProbeAsync(string ffmpegPath, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-encoders");

            using var process = Process.Start(psi);
            if (process is null) return [];

            // -encoders is a one-shot listing command, not a long-running stream — a generous but
            // bounded wait so a hung/misbehaving ffmpeg binary can't block node startup forever.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return WithoutUnusableVaapi(ParseEncodersOutput(stdout), VaapiDevice.Path is not null);
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            return [];
        }
    }

    /// <summary>ffmpeg lists every hardware encoder it was built with (distribution and Windows builds
    /// carry NVENC, QSV, and AMF), whether or not the machine has the GPU; choosing one without it fails
    /// every privacy-masked recording and export conversion. Each family is kept only when its GPU maker
    /// was detected: NVENC for NVIDIA, QSV for Intel, AMF for AMD. Software encoders always stay.</summary>
    public static IReadOnlyList<string> WithoutMissingHardware(IReadOnlyList<string> encoders, bool nvidia, bool intel, bool amd) =>
        encoders.Where(e =>
            e.EndsWith("_nvenc", StringComparison.Ordinal) ? nvidia
            : e.EndsWith("_qsv", StringComparison.Ordinal) ? intel
            : !e.EndsWith("_amf", StringComparison.Ordinal) || amd).ToList();

    /// <summary>ffmpeg lists its VAAPI encoders whether or not the machine has a GPU they can run on;
    /// they're only reported when there's an Intel or AMD render node (<see cref="VaapiDevice"/>).</summary>
    internal static IReadOnlyList<string> WithoutUnusableVaapi(IReadOnlyList<string> encoders, bool haveVaapiDevice) =>
        haveVaapiDevice ? encoders : encoders.Where(e => !e.EndsWith("_vaapi", StringComparison.Ordinal)).ToList();

    /// <summary>Whether this ffmpeg build has the <c>libwebp</c> encoder — gates whether the node
    /// writes hover thumbnails and snapshot crops as WebP (smaller/faster) or falls back to JPEG.
    /// Same never-throws, best-effort contract as <see cref="ProbeAsync"/>: a probe failure just
    /// means "assume no WebP", which is always safe. Not folded into <see cref="KnownEncoders"/> —
    /// that closed list is the heartbeat's hardware-encoder report, a different concern.</summary>
    public static async Task<bool> HasLibWebpAsync(string ffmpegPath, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-encoders");

            using var process = Process.Start(psi);
            if (process is null) return false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return ListsEncoder(stdout, "libwebp");
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            return false;
        }
    }

    /// <summary>Whether this ffmpeg loses a camera's AAC audio when it's copied: ffmpeg 7.1 stopped
    /// treating every AAC packet as a keyframe, and its RTSP reader doesn't mark them (8.0 does), so
    /// "-c copy" waits for a keyframe that never comes. The file still declares an audio track, and a
    /// browser can't play video beside an empty one. Debian 13 ships 7.1. Never throws; a probe that
    /// fails means "not affected".</summary>
    public static async Task<bool> CopyDropsAacAsync(string ffmpegPath, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null) return false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return IsFfmpegThatDropsAac(stdout);
        }
        catch (Exception) when (ct.IsCancellationRequested is false)
        {
            return false;
        }
    }

    /// <summary>Reads the libavformat line of `ffmpeg -version` ("libavformat    61.  7.103 / 61.  7.103"):
    /// 61.7 is exactly the 7.1 releases, whatever the distro calls its build.</summary>
    internal static bool IsFfmpegThatDropsAac(string versionOutput) => Libavformat61Dot7.IsMatch(versionOutput);

    [System.Text.RegularExpressions.GeneratedRegex(@"\blibavformat\s+61\.\s*7\.")]
    private static partial System.Text.RegularExpressions.Regex Libavformat61Dot7 { get; }

    /// <summary>True when a real `ffmpeg -encoders` line names <paramref name="encoderName"/> as its
    /// second whitespace-delimited token — the same exact-equality match <see cref="ParseEncodersOutput"/>
    /// uses, so a hypothetical "libwebp_anim" couldn't be mistaken for "libwebp".</summary>
    internal static bool ListsEncoder(string stdout, string encoderName)
    {
        foreach (var rawLine in stdout.Split('\n'))
        {
            var parts = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] == encoderName) return true;
        }
        return false;
    }

    // Real `ffmpeg -encoders` output (to stdout, unlike most other ffmpeg diagnostics which go to
    // stderr) looks like:
    //   Encoders:
    //    V..... = Video
    //    ......
    //    V..... libx264              libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
    //    V..X.. h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
    //    V....D h264_qsv             H.264 / AVC (Intel Quick Sync Video acceleration) (codec h264)
    // The second whitespace-delimited token on a real encoder line is always the exact ffmpeg name —
    // matched by exact equality against KnownEncoders, not substring, so e.g. a future "h264_qsv2"
    // couldn't be mistaken for "h264_qsv".
    internal static IReadOnlyList<string> ParseEncodersOutput(string stdout)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in stdout.Split('\n'))
        {
            var parts = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            if (KnownEncoders.Contains(parts[1])) found.Add(parts[1]);
        }
        // Filtered against KnownEncoders (not found.ToList()) so the result is always in the same,
        // known order regardless of how ffmpeg happens to order its own listing — a stable order is
        // one less thing for a future admin-page diff or test assertion to worry about.
        return KnownEncoders.Where(found.Contains).ToList();
    }
}
