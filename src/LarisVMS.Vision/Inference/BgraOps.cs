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
    /// <see cref="SnapshotImageCapture.MaxDimension"/>, and WebP-encodes (<paramref name="quality"/>
    /// 0-100). Null on any failure or a degenerate rectangle.</summary>
    public static byte[]? CropRectToWebp(byte[] bgra, int frameW, int frameH, SKRectI rectPx, int quality)
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
                using (var data = image.Encode(SKEncodedImageFormat.Webp, quality))
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

}
