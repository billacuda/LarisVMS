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
/// M7 pass 2 (hover thumbnails): output defaults to capped at 150px on its longer edge with aspect
/// ratio preserved (never upscaled, never distorted, never padded) and compressed harder than
/// SnapshotCapture's live grab (-q:v 8 vs 3) — a small glance-preview has no need for the higher
/// quality a full-size still does, and every thumbnail this produces gets cached indefinitely
/// alongside its source segment (see StorageManager's eviction wiring), so keeping each one small
/// matters more here than it does for a one-off snapshot.
///
/// M18 follow-up: maxDimension is now a parameter, not a constant — Pages/Snapshots' own cards
/// request a genuinely detailed frame (854px, ~480p on a 16:9 source) via this same path, since a
/// motion-event card is something a viewer actually looks closely at, unlike a fleeting scrub-hover
/// preview. The default stays 150 so every hover-preview and backfill caller is unaffected.
/// </summary>
public static class ThumbnailCapture
{
    public const int DefaultMaxDimension = 150;

    /// <summary>ffmpeg -q:v for a hover-sized preview. Deliberately lossy: at 150px nobody is reading
    /// detail out of it, and these are cached indefinitely alongside their segment.</summary>
    public const int DefaultQuality = 8;

    /// <summary>libwebp -quality (0-100, higher is better — the opposite sense to -q:v) used for the
    /// WebP path. Even a hover-detailed 854/1280px card at this level lands well under the old JPEG
    /// bytes; a 150px preview is visually identical.</summary>
    public const int DefaultWebpQuality = 80;

    /// <summary>Returns JPEG bytes (or WebP bytes when <paramref name="webp"/> is set and this
    /// ffmpeg has libwebp), or null if ffmpeg produced nothing (corrupt/truncated segment, offset
    /// beyond the file's actual content, timeout) — callers turn that into a 502 rather than this
    /// class deciding what an HTTP failure should look like. lowPriority runs the ffmpeg process at
    /// BelowNormal OS priority — set by the background backfill loop (ThumbnailBackfillService) so
    /// its catch-up work never meaningfully contends with live recording or an on-demand hover for
    /// CPU; on-demand callers leave this false since a user is actively waiting on those.</summary>
    public static async Task<byte[]?> CaptureAsync(string ffmpegPath, string filePath, int offsetSeconds, CancellationToken ct, TimeSpan? timeout = null, bool lowPriority = false, int maxDimension = DefaultMaxDimension, int quality = DefaultQuality, bool webp = false)
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
            "-vf", $"scale={maxDimension}:{maxDimension}:force_original_aspect_ratio=decrease",
            .. (webp
                ? new[] { "-c:v", "libwebp", "-quality", DefaultWebpQuality.ToString(), "-f", "image2" }
                : new[] { "-q:v", quality.ToString(), "-f", "image2" }),
            "pipe:1"
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        if (lowPriority)
        {
            // Best-effort — a process that exits before this runs (near-instant failure) just skips
            // it, which is fine, there's no CPU contention to avoid for a process that's already gone.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* already exited */ }
        }

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

    /// <summary>Writes image bytes to a hover-thumbnail (or snapshot-crop) cache file atomically —
    /// shared by the on-demand routes and the background backfill loop so a concurrent reader
    /// of thumbPath (another hover, another backfill pass, a second browser tab) never sees a
    /// partially-written file: written to a per-call-unique temp file first, then moved into place
    /// with File.Move, an atomic rename on the same volume. Best-effort — swallows IOException (a
    /// storage hiccup, or another writer finishing first for the same bucket; the two results are
    /// content-identical anyway, so losing the race here costs nothing).</summary>
    public static async Task SaveToCacheAsync(string thumbPath, byte[] bytes, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
            var tempPath = thumbPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, bytes, ct);
                File.Move(tempPath, thumbPath, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
                throw;
            }
        }
        catch (IOException) { /* concurrent writer already has it, or storage hiccup — not fatal */ }
    }
}
