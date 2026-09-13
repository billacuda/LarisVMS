using SkiaSharp;
using LarisVMS.Media;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Plain byte-level operations on packed nv12 frames (Y plane <c>w*h</c>, then interleaved
/// UV <c>w*h/2</c>) — a byte copy or a bilinear resize, no SkiaSharp scaling of a full frame, no
/// CPU per-pixel normalize loop.
/// </summary>
public static class Nv12Ops
{
    /// <summary>Carves an even-aligned <paramref name="x"/>,<paramref name="y"/>,
    /// <paramref name="w"/>,<paramref name="h"/> rectangle out of a source nv12 frame into its own
    /// packed nv12 buffer (<c>w*h*3/2</c> bytes). Pure byte copies. Currently unused — its caller
    /// (high-res re-detection's tile placement) was removed; kept for the planned slicing overhaul,
    /// whose fixed-grid tiles will also always be even (clamped to the 640 network size and the
    /// frame's own even dimensions).</summary>
    public static byte[] CropTile(byte[] src, int srcW, int srcH, int x, int y, int w, int h)
    {
        ArgumentNullException.ThrowIfNull(src);
        var dst = new byte[w * h * 3 / 2];

        // Y
        for (var row = 0; row < h; row++)
            Array.Copy(src, (y + row) * srcW + x, dst, row * w, w);

        // interleaved UV — half the rows, same byte width (x is even, so x bytes = x/2 UV pairs)
        var srcUv = srcW * srcH;
        var dstUv = w * h;
        for (var crow = 0; crow < h / 2; crow++)
            Array.Copy(src, srcUv + (y / 2 + crow) * srcW + x, dst, dstUv + crow * w, w);

        return dst;
    }

    /// <summary>Bilinear-resizes a source nv12 frame to <paramref name="profile"/>'s
    /// <see cref="InferenceProfile.ScaledWidth"/>×<see cref="InferenceProfile.ScaledHeight"/> and
    /// centre-pads it to the network size (Y = 16, U/V = 128 — the limited-range black the model's
    /// letterbox bars expect), at <paramref name="profile"/>'s own <see cref="InferenceProfile.PadLeft"/>/
    /// <see cref="InferenceProfile.PadTop"/>. Taking the profile directly guarantees the geometry
    /// matches what <see cref="DFineDecoder"/> uses to map whole-frame boxes back.</summary>
    public static byte[] LetterboxTo(byte[] src, int srcW, int srcH, InferenceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int netW = profile.NetworkWidth, netH = profile.NetworkHeight;
        int sw = profile.ScaledWidth, sh = profile.ScaledHeight;
        int padL = profile.PadLeft, padT = profile.PadTop;

        var dst = new byte[netW * netH * 3 / 2];
        Array.Fill(dst, (byte)16, 0, netW * netH);
        Array.Fill(dst, (byte)128, netW * netH, netW * netH / 2);

        var srcUv = srcW * srcH;
        var dstUv = netW * netH;

        // Y: srcW×srcH -> sw×sh, placed at (padL, padT)
        var xRatio = (double)srcW / sw;
        var yRatio = (double)srcH / sh;
        for (var oy = 0; oy < sh; oy++)
        {
            var fy = (oy + 0.5) * yRatio - 0.5;
            var (y0, y1, wy) = Weights(fy, srcH);
            var dstRow = (padT + oy) * netW + padL;
            for (var ox = 0; ox < sw; ox++)
            {
                var fx = (ox + 0.5) * xRatio - 0.5;
                var (x0, x1, wx) = Weights(fx, srcW);
                dst[dstRow + ox] = Bilerp(
                    src[y0 * srcW + x0], src[y0 * srcW + x1],
                    src[y1 * srcW + x0], src[y1 * srcW + x1], wx, wy);
            }
        }

        // U and V: (srcW/2)×(srcH/2) each -> (sw/2)×(sh/2), interleaved, at (padL, padT/... )
        int cSrcW = srcW / 2, cSrcH = srcH / 2, cSw = sw / 2, cSh = sh / 2;
        var cxRatio = (double)cSrcW / cSw;
        var cyRatio = (double)cSrcH / cSh;
        for (var oy = 0; oy < cSh; oy++)
        {
            var fy = (oy + 0.5) * cyRatio - 0.5;
            var (y0, y1, wy) = Weights(fy, cSrcH);
            var dstRow = dstUv + (padT / 2 + oy) * netW + padL;
            for (var ox = 0; ox < cSw; ox++)
            {
                var fx = (ox + 0.5) * cxRatio - 0.5;
                var (x0, x1, wx) = Weights(fx, cSrcW);
                // source U at srcUv + row*srcW + col*2 ; V at +1
                var u00 = src[srcUv + y0 * srcW + x0 * 2]; var u01 = src[srcUv + y0 * srcW + x1 * 2];
                var u10 = src[srcUv + y1 * srcW + x0 * 2]; var u11 = src[srcUv + y1 * srcW + x1 * 2];
                var v00 = src[srcUv + y0 * srcW + x0 * 2 + 1]; var v01 = src[srcUv + y0 * srcW + x1 * 2 + 1];
                var v10 = src[srcUv + y1 * srcW + x0 * 2 + 1]; var v11 = src[srcUv + y1 * srcW + x1 * 2 + 1];
                dst[dstRow + ox * 2] = Bilerp(u00, u01, u10, u11, wx, wy);
                dst[dstRow + ox * 2 + 1] = Bilerp(v00, v01, v10, v11, wx, wy);
            }
        }

        return dst;
    }

    /// <summary>Eager snapshot crop (pass 3d/4b): expands <paramref name="boxPx"/> via
    /// <see cref="SnapshotImageCapture.ComputeCropRect"/>, converts just that region of the nv12
    /// frame to RGB (BT.601), downscales so the longer edge is at most
    /// <see cref="SnapshotImageCapture.MaxDimension"/>, and WebP-encodes (<paramref name="quality"/>
    /// 0-100). Region-sized, not frame-sized — the Skia cost is trivial. Null on any failure.
    /// <paramref name="marginFraction"/> defaults to <see cref="SnapshotImageCapture.DefaultMarginFraction"/>
    /// (30%) for source compatibility, but every caller in this codebase now passes the operator's
    /// own Detection.SnapshotMarginPercent explicitly (see
    /// <c>CameraDetectionPipeline.TrySubFrameSnapshot</c>/<c>TrySliceSnapshot</c>) — before this,
    /// this path silently used the 30% default while <see cref="BgraOps.CropRectToWebp"/>'s caller
    /// margined its rect at 12% (or, in slice mode, not at all), so the two crop helpers disagreed
    /// on how much context a snapshot showed depending only on which pixel format the frame
    /// happened to be in.</summary>
    public static byte[]? CropToWebp(byte[] src, int srcW, int srcH, SKRectI boxPx, int quality,
        double marginFraction = SnapshotImageCapture.DefaultMarginFraction)
    {
        try
        {
            var (cx, cy, cw, ch) = SnapshotImageCapture.ComputeCropRect(
                boxPx.Left / (double)srcW, boxPx.Top / (double)srcH,
                boxPx.Width / (double)srcW, boxPx.Height / (double)srcH, srcW, srcH, marginFraction);

            using var region = RegionToBitmap(src, srcW, srcH, cx, cy, cw, ch);
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
                return data.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Full nv12 frame → BGRA <see cref="SKBitmap"/> for a diagnostic dump. Caller disposes.</summary>
    public static SKBitmap ToDebugBitmap(byte[] src, int w, int h) => RegionToBitmap(src, w, h, 0, 0, w, h);

    private static SKBitmap RegionToBitmap(byte[] src, int srcW, int srcH, int rx, int ry, int rw, int rh)
    {
        var bmp = new SKBitmap(rw, rh, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pixels = bmp.GetPixelSpan();
        var srcUv = srcW * srcH;
        for (var y = 0; y < rh; y++)
        {
            var sy = ry + y;
            var cyRow = srcUv + (sy / 2) * srcW;
            for (var x = 0; x < rw; x++)
            {
                var sx = rx + x;
                float yv = src[sy * srcW + sx];
                var cxByte = (sx / 2) * 2;
                float u = src[cyRow + cxByte];
                float v = src[cyRow + cxByte + 1];
                var (r, g, b) = Bt601Limited.ToRgb(yv, u, v);
                var o = (y * rw + x) * 4;
                pixels[o] = b; pixels[o + 1] = g; pixels[o + 2] = r; pixels[o + 3] = 255;
            }
        }
        return bmp;
    }

    private static (int Lo, int Hi, double Frac) Weights(double f, int max)
    {
        if (f <= 0) return (0, 0, 0);
        var lo = (int)Math.Floor(f);
        if (lo >= max - 1) return (max - 1, max - 1, 0);
        return (lo, lo + 1, f - lo);
    }

    private static byte Bilerp(byte a, byte b, byte c, byte d, double wx, double wy)
    {
        var top = a + (b - a) * wx;
        var bot = c + (d - c) * wx;
        var val = top + (bot - top) * wy;
        return (byte)(val < 0 ? 0 : val > 255 ? 255 : val + 0.5);
    }
}
