using System.Runtime.InteropServices;
using LarisVMS.Media;
using SkiaSharp;

namespace LarisVMS.Vision.Inference;

/// <summary>Byte-level crop/encode for the raw BGRA8888 frame buffer the continuous detection loop
/// holds (the default — <c>Detection.GpuPreprocessing</c> off). The nv12 sibling is
/// <see cref="Nv12Ops"/>. Pass G: the snapshot is cropped straight out of the frame the model ran
/// on, so the box can't have moved off it.</summary>
public static class BgraOps
{
    /// <summary>Crops <paramref name="rectPx"/> out of a <paramref name="frameW"/>×<paramref name="frameH"/>
    /// BGRA8888 buffer, downscales so the longer edge is at most
    /// <see cref="SnapshotImageCapture.MaxDimension"/>, and JPEG-encodes. Null on any failure or a
    /// degenerate rectangle.</summary>
    public static byte[]? CropRectToJpeg(byte[] bgra, int frameW, int frameH, SKRectI rectPx, int quality)
    {
        try
        {
            var cx = Math.Clamp(rectPx.Left, 0, Math.Max(0, frameW - 2));
            var cy = Math.Clamp(rectPx.Top, 0, Math.Max(0, frameH - 2));
            var cw = Math.Clamp(rectPx.Width, 2, frameW - cx);
            var ch = Math.Clamp(rectPx.Height, 2, frameH - cy);

            if (bgra.Length < (long)frameW * frameH * 4) return null;

            var info = new SKImageInfo(frameW, frameH, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
            try
            {
                using var full = new SKBitmap();
                if (!full.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes)) return null;

                // Blit the region into its own owned bitmap — independent of the pinned buffer, so
                // the rest (resize, encode) is safe after the handle is freed.
                using var region = new SKBitmap(new SKImageInfo(cw, ch, SKColorType.Bgra8888, SKAlphaType.Opaque));
                using (var canvas = new SKCanvas(region))
                    canvas.DrawBitmap(full, new SKRect(cx, cy, cx + cw, cy + ch), new SKRect(0, 0, cw, ch));

                var scale = Math.Min(1.0, SnapshotImageCapture.MaxDimension / (double)Math.Max(cw, ch));
                SKBitmap toEncode = region;
                SKBitmap? scaled = null;
                if (scale < 1.0)
                {
                    scaled = region.Resize(
                        new SKImageInfo(Math.Max(1, (int)Math.Round(cw * scale)), Math.Max(1, (int)Math.Round(ch * scale))),
                        SKSamplingOptions.Default);
                    if (scaled is not null) toEncode = scaled;
                }

                using (scaled)
                using (var image = SKImage.FromBitmap(toEncode))
                using (var data = image.Encode(SKEncodedImageFormat.Jpeg, quality))
                    return data?.ToArray();
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Pass F: downscales a <paramref name="capW"/>×<paramref name="capH"/> BGRA8888 capture
    /// buffer into the network-input buffer <paramref name="dest"/> (<c>profile.NetworkWidth</c>×
    /// <c>NetworkHeight</c>×4 bytes, reused across frames), applying <paramref name="profile"/>'s own
    /// scale + pad geometry so the result is byte-for-byte what the engine would have received had
    /// ffmpeg produced the network size directly. Pad bars are filled RGB 0, matching the
    /// <c>color=black</c> pad in VisionSession's own filter chain. Returns <paramref name="dest"/>.
    /// Throws on a Skia failure — the caller's inference loop treats that as a skipped frame, same as
    /// an inference exception.</summary>
    public static byte[] LetterboxResize(byte[] capture, int capW, int capH, InferenceProfile profile, byte[] dest)
    {
        var netW = profile.NetworkWidth;
        var netH = profile.NetworkHeight;
        var content = profile.ContentRect; // scaled content rect inside the network buffer (no pad for Stretch)

        var srcInfo = new SKImageInfo(capW, capH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        if (capture.Length < (long)capW * capH * 4)
            throw new InvalidOperationException(
                $"Capture buffer is {capture.Length} bytes, expected {(long)capW * capH * 4} for {capW}x{capH}.");

        var handle = GCHandle.Alloc(capture, GCHandleType.Pinned);
        try
        {
            using var src = new SKBitmap();
            if (!src.InstallPixels(srcInfo, handle.AddrOfPinnedObject(), srcInfo.RowBytes))
                throw new InvalidOperationException("LetterboxResize: InstallPixels failed on the capture buffer.");

            using var scaled = src.Resize(
                new SKImageInfo(content.Width, content.Height, SKColorType.Bgra8888, SKAlphaType.Opaque),
                SKSamplingOptions.Default)
                ?? throw new InvalidOperationException("LetterboxResize: SKBitmap.Resize returned null.");

            using var target = new SKBitmap(new SKImageInfo(netW, netH, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(target))
            {
                var padded = content.Left != 0 || content.Top != 0 || content.Width != netW || content.Height != netH;
                if (padded) canvas.Clear(new SKColor(0, 0, 0));
                canvas.DrawBitmap(scaled, content.Left, content.Top);
            }

            target.GetPixelSpan().CopyTo(dest);
            return dest;
        }
        finally
        {
            handle.Free();
        }
    }
}
