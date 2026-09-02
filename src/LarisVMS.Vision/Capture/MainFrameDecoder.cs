using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LarisVMS.Vision.Capture;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 3b/4b: decodes ONE full-resolution Main-stream
/// frame from the compressed bytes a node's MainFrameRingBuffer hands over (init segment + one fMP4
/// fragment), as raw packed <b>nv12</b> — the one genuinely new ffmpeg-invocation shape in this
/// overhaul, feeding ffmpeg bytes via a pipe instead of a file path or RTSP URL.
///
/// hands ffmpeg's own mov/mp4 demuxer a standard fragmented-MP4 byte stream (exactly what
/// RecordingSession's live tee already produces for browser MSE playback) and lets ffmpeg's normal
/// decode pipeline handle any hvcC framing itself — no manual bitstream conversion.
///
/// Pass 4b: output is nv12 (W*H*3/2 bytes) not BGRA (W*H*4), and — where the node has CUDA — NVDEC
/// decodes it with the same <c>scale_cuda</c>/<c>hwdownload</c> chain <see cref="VisionSession"/>
/// already runs against a live stream. Everything downstream (the letterbox whole-frame pass, every
/// native-scale tile crop, the eager snapshot crop) then works on this one nv12 buffer via plain
/// byte copies and the accelerator's own ONNX preprocessing head — no SkiaSharp scaling of a 19 MB
/// frame and no CPU per-pixel normalize loop.
/// </summary>
public static class MainFrameDecoder
{
    /// <summary>Returns the decoded frame as packed nv12 (<paramref name="frameWidth"/> *
    /// <paramref name="frameHeight"/> Y bytes, then half that many interleaved-UV bytes), or null if
    /// ffmpeg produced nothing (malformed/incomplete fragment, timeout, NVDEC couldn't handle it, or
    /// a frame size that doesn't match what was requested) — same null-means-capture-failure
    /// convention ThumbnailCapture/SnapshotImageCapture already use. Both dimensions are forced even
    /// (nv12 requires it); an odd input is rounded down.</summary>
    public static async Task<byte[]?> DecodeNv12Async(string ffmpegPath, byte[] initSegment, byte[] fragment,
        int frameWidth, int frameHeight, CancellationToken ct, ILogger? logger = null, TimeSpan? timeout = null,
        string? hardwareAcceleration = null)
    {
        frameWidth &= ~1;
        frameHeight &= ~1;
        if (frameWidth < 2 || frameHeight < 2) return null;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // -f mp4 named explicitly rather than relying on format auto-probe against a raw pipe — the
        // container is known (RecordingSession's own live-tee fragments).
        //
        // An explicit scale to the requested dimensions, even though it's normally a no-op: a
        // fragment whose real resolution differs from the probed Main-stream size (a stale probe, a
        // camera re-profiled since) would otherwise emit that real size and the fixed-size read
        // below shears it. On CUDA the scale runs as scale_cuda + hwdownload (the exact chain
        // VisionSession proves against a live stream); otherwise a plain CPU scale. nv12 output
        // either way — no swscale to BGRA.
        var useCuda = string.Equals(hardwareAcceleration, "cuda", StringComparison.OrdinalIgnoreCase);
        var args = new List<string> { "-nostdin" };
        if (useCuda)
        {
            args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);
            args.AddRange(["-f", "mp4", "-i", "pipe:0", "-frames:v", "1",
                "-vf", $"scale_cuda=w={frameWidth}:h={frameHeight}:format=nv12,hwdownload,format=nv12"]);
        }
        else
        {
            args.AddRange(["-f", "mp4", "-i", "pipe:0", "-frames:v", "1",
                "-vf", $"scale={frameWidth}:{frameHeight}"]);
        }
        args.AddRange(["-pix_fmt", "nv12", "-f", "rawvideo", "pipe:1"]);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(8));

        var stderrDrain = process.StandardError.ReadToEndAsync(CancellationToken.None);
        // Written concurrently with the stdout read below — an init segment plus fragment can exceed
        // the OS pipe buffer and ffmpeg may need to start producing output before it has consumed
        // all input for a clip this short.
        var writeTask = WriteInputAsync(process, initSegment, fragment, ct);

        var frameSize = checked(frameWidth * frameHeight * 3 / 2); // nv12
        var buffer = new byte[frameSize];
        bool complete;
        try
        {
            complete = await ReadExactAsync(process.StandardOutput.BaseStream, buffer, frameSize, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            logger?.LogWarning("Main-frame decode timed out after {Timeout}s ({Width}x{Height}).", (timeout ?? TimeSpan.FromSeconds(8)).TotalSeconds, frameWidth, frameHeight);
            return null;
        }
        finally
        {
            if (!process.HasExited) TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            var stderr = await stderrDrain;
            try { await writeTask; } catch { /* ffmpeg may have exited before consuming everything */ }
            if (!string.IsNullOrWhiteSpace(stderr)) logger?.LogDebug("ffmpeg (main-frame decode): {Stderr}", stderr.Trim());
        }

        if (!complete)
        {
            logger?.LogWarning("Main-frame decode produced fewer than {Expected} bytes for a {Width}x{Height} nv12 frame — fragment likely malformed or NVDEC couldn't decode it.", frameSize, frameWidth, frameHeight);
            return null;
        }

        return buffer;
    }

    private static async Task WriteInputAsync(Process process, byte[] initSegment, byte[] fragment, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(initSegment, ct);
            await process.StandardInput.BaseStream.WriteAsync(fragment, ct);
            await process.StandardInput.BaseStream.FlushAsync(ct);
        }
        catch (Exception) { /* ffmpeg may exit (e.g. on a malformed fragment) before consuming everything — the stdout read decides success */ }
        finally
        {
            process.StandardInput.Close();
        }
    }

    // Same contract as VisionSession.ReadExactAsync / MotionSession's own read loop.
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* already exited */ }
    }
}
