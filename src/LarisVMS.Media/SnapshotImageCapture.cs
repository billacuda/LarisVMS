using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Media;

/// <summary>
/// Object detection plan decision 10: one-shot cropped still-frame grab from an already-recorded
/// segment file — a sibling to ThumbnailCapture, not a variant of it, deliberately kept as its own
/// class/cache tier per the user's own ask that this be genuinely separate from the existing
/// thumbnail tiers rather than just a bigger version of the same one. Shares ThumbnailCapture's
/// process-invocation shape (input-side -ss, redirected pipe:1 output, stderr drain, timeout/kill)
/// almost line for line, with two differences: a `crop=` filter always runs ahead of the `scale=`
/// one, and the output is capped at 720p rather than 150/854px — meaningfully more detail than even
/// Snapshots' own card thumbnail for a viewer who wants to actually look closely at what was
/// detected, still nowhere near a 4K Main stream's native resolution.
///
/// Cached separately from thumbnails (cam-{id}/snapshots/, keyed by the owning MotionSpan's own id
/// rather than a bucketed time offset — see NodeWorker/Program.cs's own route for why a snapshot's
/// one owning span makes that simpler than the generic hover-scrub's offset-bucketing scheme).
/// </summary>
public static class SnapshotImageCapture
{
    /// <summary>Cap on the longer edge — 1280 on a 16:9 source yields 1280x720, matching the plan's
    /// own "720p at max" ask exactly.</summary>
    public const int MaxDimension = 1280;

    /// <summary>ffmpeg -q:v — matches Snapshots' own "exact" card thumbnail quality (ThumbnailCapture's
    /// exact=true path): a viewer looking closely at a cropped detection deserves the same fidelity
    /// as the uncropped card it sits alongside, not the heavier hover-preview compression.</summary>
    public const int DefaultQuality = 4;

    /// <summary>Fraction of the box's own width/height added as margin on every side before cropping —
    /// a razor-tight crop on just the reported box reads as an odd, context-free sliver; a little
    /// surrounding scene makes it obvious what's actually in frame. Tuned up from an initial 0.15
    /// once this shipped against real cameras: the best-frame box's own timestamp (Vision Service's
    /// wall-clock processing time on the Sub stream) doesn't correspond exactly to the same instant
    /// in the recorded Main-stream segment this crops from — Sub-stream detection latency and
    /// Main-stream decode/network latency aren't identical, so a moving object can have shifted
    /// slightly by the time the crop is actually taken (confirmed live: the same lag is visible on
    /// the *live* detection overlay, which runs boxes slightly ahead of the video it's drawn over).
    /// A box-relative-only margin masked this well enough with YOLOv9's looser boxes; D-FINE's
    /// tighter, more accurate ones leave much less absolute slack at the same fraction. See
    /// <see cref="DefaultMinFrameMarginFraction"/> for the other half of this fix.</summary>
    public const double DefaultMarginFraction = 0.3;

    /// <summary>A floor on the margin, as a fraction of the *frame's* own dimensions rather than the
    /// box's — a small/distant object's own box can be tiny, and <see cref="DefaultMarginFraction"/>
    /// alone would then add almost no absolute pixels of slack, which is backwards: the position
    /// drift the margin exists to absorb (see that constant's own doc comment) doesn't shrink just
    /// because the detected object's box happened to be small. Whichever of the two margins is
    /// larger wins, per axis.</summary>
    public const double DefaultMinFrameMarginFraction = 0.05;

    /// <summary>Computes the pixel crop rectangle for a normalized (0-1) detection box against a
    /// frameWidth x frameHeight source frame, expanding by whichever of marginFraction (relative to
    /// the box) or minFrameMarginFraction (relative to the frame) is larger on every side, and
    /// clamping to frame bounds. Pure and unit-tested directly, same reasoning as ThumbnailCapture's
    /// own process-invocation/pure-logic split elsewhere in this codebase. Never returns a rectangle
    /// narrower/shorter than 2px (ffmpeg's crop filter requires a positive size) — a degenerate
    /// (near-zero) reported box still produces something croppable rather than a filter error.</summary>
    internal static (int X, int Y, int W, int H) ComputeCropRect(
        double boxX, double boxY, double boxW, double boxH,
        int frameWidth, int frameHeight, double marginFraction = DefaultMarginFraction,
        double minFrameMarginFraction = DefaultMinFrameMarginFraction)
    {
        var marginX = Math.Max(boxW * marginFraction, minFrameMarginFraction);
        var marginY = Math.Max(boxH * marginFraction, minFrameMarginFraction);

        var x0 = Math.Clamp(boxX - marginX, 0, 1);
        var y0 = Math.Clamp(boxY - marginY, 0, 1);
        var x1 = Math.Clamp(boxX + boxW + marginX, 0, 1);
        var y1 = Math.Clamp(boxY + boxH + marginY, 0, 1);

        var pxX = (int)Math.Round(x0 * frameWidth);
        var pxY = (int)Math.Round(y0 * frameHeight);
        var pxW = Math.Max(2, (int)Math.Round((x1 - x0) * frameWidth));
        var pxH = Math.Max(2, (int)Math.Round((y1 - y0) * frameHeight));

        // Clamp the rectangle itself to frame bounds — a box near the frame's own edge, expanded by
        // margin, can otherwise ask ffmpeg's crop filter for a region that runs past the source frame,
        // which it rejects outright rather than silently clamping.
        pxX = Math.Clamp(pxX, 0, Math.Max(0, frameWidth - 2));
        pxY = Math.Clamp(pxY, 0, Math.Max(0, frameHeight - 2));
        pxW = Math.Clamp(pxW, 2, frameWidth - pxX);
        pxH = Math.Clamp(pxH, 2, frameHeight - pxY);

        return (pxX, pxY, pxW, pxH);
    }

    /// <summary>Returns JPEG bytes, or null if ffmpeg produced nothing (corrupt/truncated segment,
    /// offset beyond the file's actual content, timeout, or a crop rectangle ffmpeg otherwise
    /// rejects) — callers turn that into a 502 rather than this class deciding what an HTTP failure
    /// should look like, same convention ThumbnailCapture.CaptureAsync already uses. On a null result,
    /// logs ffmpeg's own stderr (if any logger is supplied) — the empty-output case alone doesn't say
    /// *why* ffmpeg produced nothing, and every failure reason this doc comment lists produces its own
    /// distinct ffmpeg error text.</summary>
    public static async Task<byte[]?> CaptureAsync(string ffmpegPath, string filePath, int offsetSeconds,
        double boxX, double boxY, double boxW, double boxH, int frameWidth, int frameHeight,
        CancellationToken ct, TimeSpan? timeout = null, ILogger? logger = null)
    {
        var (cropX, cropY, cropW, cropH) = ComputeCropRect(boxX, boxY, boxW, boxH, frameWidth, frameHeight);

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
            "-vf", $"crop={cropW}:{cropH}:{cropX}:{cropY},scale={MaxDimension}:{MaxDimension}:force_original_aspect_ratio=decrease",
            "-q:v", DefaultQuality.ToString(),
            "-f", "image2",
            "pipe:1"
        ];
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");

        // Same timeout reasoning as ThumbnailCapture.CaptureAsync — a local (or SMB) file read has no
        // RTSP handshake to wait on, but a slow SMB share is a real, confirmed possibility.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));

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
            logger?.LogWarning("Snapshot-image capture for {FilePath} at offset {OffsetSeconds}s timed out.", filePath, offsetSeconds);
            return null;
        }
        finally
        {
            if (!process.HasExited) TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await stderrDrain;
        }

        if (bytes.Length > 0) return bytes;

        logger?.LogWarning("Snapshot-image capture for {FilePath} at offset {OffsetSeconds}s (crop={CropW}:{CropH}:{CropX}:{CropY}) produced no bytes. ffmpeg stderr: {Stderr}",
            filePath, offsetSeconds, cropW, cropH, cropX, cropY, (await stderrDrain).Trim());
        return null;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
    }
}
