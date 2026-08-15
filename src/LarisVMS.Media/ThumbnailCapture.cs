using System.Diagnostics;

namespace LarisVMS.Media;

/// <summary>
/// One-shot still-frame grab from an already-recorded segment file at a given offset — no relation
/// to SnapshotCapture (a fresh RTSP session against the live camera; never reads a file on disk) or
/// RecordingSession's tee. Kept as its own class rather than a second method on SnapshotCapture
/// specifically because SnapshotCapture's own doc comment frames it as "a camera's own RTSP
/// stream" — extending it would blur that file-vs-live boundary instead of keeping it explicit in
/// the type itself. Input-side -ss (before -i) is used deliberately: fast and roughly
/// keyframe-accurate for a local file, unlike SnapshotCapture's RTSP-side connection timeout.
///
/// M7 pass 2 (hover thumbnails): output is capped at 150px on its longer edge with aspect ratio
/// preserved (never upscaled, never distorted, never padded) and compressed harder than
/// SnapshotCapture's live grab (-q:v 8 vs 3) — a small glance-preview has no need for the higher
/// quality a full-size still does, and every thumbnail this produces gets cached indefinitely
/// alongside its source segment (see StorageManager's eviction wiring), so keeping each one small
/// matters more here than it does for a one-off snapshot.
/// </summary>
public static class ThumbnailCapture
{
    /// <summary>Returns JPEG bytes, or null if ffmpeg produced nothing (corrupt/truncated segment,
    /// offset beyond the file's actual content, timeout) — callers turn that into a 502 rather than
    /// this class deciding what an HTTP failure should look like.</summary>
    public static async Task<byte[]?> CaptureAsync(string ffmpegPath, string filePath, int offsetSeconds, CancellationToken ct, TimeSpan? timeout = null)
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

        string[] args =
        [
            "-nostdin",
            "-ss", offsetSeconds.ToString(),
            "-i", filePath,
            "-frames:v", "1",
            "-vf", "scale=150:150:force_original_aspect_ratio=decrease",
            "-q:v", "8",
            "-f", "image2",
            "pipe:1"
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");

        // Tighter than SnapshotCapture's 10s default — a local (or SMB) file read has no RTSP
        // handshake to wait on, but StorageManager's own comments already acknowledge a slow SMB
        // share is a real, confirmed possibility, so this isn't cut to sub-second either.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));

        // Drain stderr concurrently — same deadlock-avoidance reason as SnapshotCapture/RecordingSession.
        var stderrDrain = process.StandardError.ReadToEndAsync(CancellationToken.None);

        byte[] bytes;
        try
        {
            using var buffer = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, timeoutCts.Token);
            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return null;
        }
        finally
        {
            if (!process.HasExited) TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await stderrDrain;
        }

        return bytes.Length > 0 ? bytes : null;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
    }
}
