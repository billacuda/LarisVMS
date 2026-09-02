using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>Pass 4b: byte-level nv12 ops that replace the SkiaSharp scaling/cropping in the
/// high-res re-detection path.</summary>
public class Nv12OpsTests
{
    // Builds an w*h*3/2 nv12 buffer where Y[y*w+x] = yFn(x,y) and the UV pair at chroma (cx,cy) is
    // (uFn(cx,cy), vFn(cx,cy)).
    private static byte[] MakeNv12(int w, int h, Func<int, int, byte> yFn, Func<int, int, byte> uFn, Func<int, int, byte> vFn)
    {
        var buf = new byte[w * h * 3 / 2];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            buf[y * w + x] = yFn(x, y);
        var uvBase = w * h;
        for (var cy = 0; cy < h / 2; cy++)
        for (var cx = 0; cx < w / 2; cx++)
        {
            buf[uvBase + cy * w + cx * 2] = uFn(cx, cy);
            buf[uvBase + cy * w + cx * 2 + 1] = vFn(cx, cy);
        }
        return buf;
    }

    [Fact]
    public void CropTile_ExtractsTheRightYAndUvBytes()
    {
        const int w = 8, h = 8;
        var src = MakeNv12(w, h,
            yFn: (x, y) => (byte)(y * 16 + x),
            uFn: (cx, cy) => (byte)(100 + cy * 10 + cx),
            vFn: (cx, cy) => (byte)(200 + cy * 10 + cx));

        var tile = new TileLayout.Tile(X: 2, Y: 2, Width: 4, Height: 4);
        var crop = Nv12Ops.CropTile(src, w, h, tile);

        Assert.Equal(4 * 4 * 3 / 2, crop.Length);

        // Y: crop[r*4+c] == src Y at (2+c, 2+r)
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
            Assert.Equal((byte)((2 + r) * 16 + (2 + c)), crop[r * 4 + c]);

        // UV: crop chroma rows 0..1 come from source chroma rows 1..2, columns from source chroma col 1
        var cUvBase = 4 * 4;
        for (var cr = 0; cr < 2; cr++)
        for (var cc = 0; cc < 2; cc++)
        {
            Assert.Equal((byte)(100 + (1 + cr) * 10 + (1 + cc)), crop[cUvBase + cr * 4 + cc * 2]);
            Assert.Equal((byte)(200 + (1 + cr) * 10 + (1 + cc)), crop[cUvBase + cr * 4 + cc * 2 + 1]);
        }
    }

    [Fact]
    public void CropTile_FullFrameIsAnIdentityCopy()
    {
        const int w = 6, h = 6;
        var src = MakeNv12(w, h, (x, y) => (byte)(x + y), (cx, cy) => (byte)(50 + cx + cy), (cx, cy) => (byte)(150 + cx + cy));
        var crop = Nv12Ops.CropTile(src, w, h, new TileLayout.Tile(0, 0, w, h));
        Assert.Equal(src, crop);
    }

    [Fact]
    public void LetterboxTo_PlacesScaledContentAtThePadOffsetsWithLimitedRangeBars()
    {
        // 1280x720 -> scale 0.5 -> 640x360 content, padTop 140, padLeft 0.
        var profile = InferenceProfile.Create(1280, 720, LarisVMS.Core.Enums.AspectMode.Letterbox, 640);
        Assert.Equal(640, profile.ScaledWidth);
        Assert.Equal(360, profile.ScaledHeight);
        Assert.Equal(140, profile.PadTop);
        Assert.Equal(0, profile.PadLeft);

        var src = MakeNv12(1280, 720, (x, y) => 130, (cx, cy) => 128, (cx, cy) => 128); // solid mid-gray
        var dst = Nv12Ops.LetterboxTo(src, 1280, 720, profile);

        Assert.Equal(640 * 640 * 3 / 2, dst.Length);

        // Top bar row (row 0) is limited-range black on Y.
        for (var x = 0; x < 640; x++) Assert.Equal(16, dst[x]);
        // First content row (140) is the scaled gray, not the bar.
        Assert.Equal(130, dst[140 * 640 + 320]);
        // Bottom bar row (639) is black again.
        for (var x = 0; x < 640; x++) Assert.Equal(16, dst[639 * 640 + x]);

        // UV bar rows are 128; content chroma is also 128 here (gray) so check a bar row explicitly.
        var uvBase = 640 * 640;
        Assert.Equal(128, dst[uvBase + 0]);            // chroma row 0 == pad
        Assert.Equal(128, dst[uvBase + (70 * 640) + 320]); // chroma row 70 (= content) still 128 for gray
    }

    [Fact]
    public void CropToJpeg_ProducesADecodableJpeg()
    {
        const int w = 128, h = 96;
        var src = MakeNv12(w, h, (x, y) => (byte)(x + y), (cx, cy) => 128, (cx, cy) => 128);
        var box = new SkiaSharp.SKRectI(20, 20, 80, 70);

        var jpeg = Nv12Ops.CropToJpeg(src, w, h, box, 80);

        Assert.NotNull(jpeg);
        Assert.True(jpeg!.Length > 100);
        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]); // JPEG SOI marker
    }
}
