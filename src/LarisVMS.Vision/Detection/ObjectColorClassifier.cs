using LarisVMS.Vision.Inference;

namespace LarisVMS.Vision.Detection;

/// <summary>Read-only RGB access to one decoded frame buffer — packed BGRA or packed nv12, the two
/// formats the detection pipeline's ffmpeg emits.</summary>
public readonly struct FramePixels(byte[] buffer, int width, int height, bool isNv12)
{
    public int Width { get; } = width;
    public int Height { get; } = height;

    public (byte R, byte G, byte B) Rgb(int x, int y)
    {
        if (isNv12)
        {
            float yv = buffer[y * Width + x];
            var uv = Width * Height + (y / 2) * Width + (x / 2) * 2;
            return Bt601Limited.ToRgb(yv, buffer[uv], buffer[uv + 1]);
        }
        var o = (y * Width + x) * 4;
        return (buffer[o + 2], buffer[o + 1], buffer[o]);
    }
}

/// <summary>A pixel rectangle inside a <see cref="FramePixels"/> buffer, right/bottom exclusive.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>The sub-rectangle covering fractions [fx0, fx1) × [fy0, fy1) of this one.</summary>
    public PixelRect Part(double fx0, double fy0, double fx1, double fy1) => new(
        Left + (int)Math.Round(Width * fx0), Top + (int)Math.Round(Height * fy0),
        Left + (int)Math.Round(Width * fx1), Top + (int)Math.Round(Height * fy1));

    public PixelRect ClampTo(int width, int height) => new(
        Math.Clamp(Left, 0, width), Math.Clamp(Top, 0, height),
        Math.Clamp(Right, 0, width), Math.Clamp(Bottom, 0, height));
}

/// <summary>The colors classified for one object. Upper/Lower are for people only.</summary>
public sealed record ObjectColorResult(string? Primary, string? Upper, string? Lower);

/// <summary>
/// Classical object color classification: no model, so it works with any detector. Samples a grid
/// of pixels from the part of the box most likely to be the object rather than background, clusters
/// them in CIE Lab with k-means (k=3), and names the biggest cluster from a fixed list
/// (<see cref="LarisVMS.Core.ObjectColors"/>). A box always holds some of the scene behind the object
/// (lawn around a dog, road around a car), so the colors just outside the box are sampled too, and a
/// cluster matching them is passed over for the next one. People get shirt and pants colors from the
/// upper and lower body bands. Returns null names when the frame is monochrome (IR night mode) —
/// gray pixels there say nothing about the real color.
/// </summary>
public static class ObjectColorClassifier
{
    // Names one object can plausibly get in two different frames (light, shade, a partial view).
    private static readonly HashSet<(string, string)> NearColors =
    [
        ("white", "silver"), ("white", "gray"), ("silver", "gray"), ("gray", "black"),
        ("red", "orange"), ("red", "pink"), ("orange", "yellow"), ("orange", "brown"), ("blue", "purple"),
    ];

    /// <summary>Whether two color names could be the same object: either unknown, the same, or
    /// close neighbours (white and silver, say).</summary>
    public static bool CouldBeSameColor(string? a, string? b)
    {
        if (a is null || b is null) return true;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        return NearColors.Contains((a, b)) || NearColors.Contains((b, a));
    }

    /// <summary>Below this many pixels on either side the crop is too small to say anything.</summary>
    public const int MinRegionPixels = 6;

    private const int MaxSamplesPerSide = 30;
    private const int Clusters = 3;
    private const int Iterations = 8;

    /// <summary>Chroma (Lab √(a²+b²)) below which a color reads as black/gray/white.</summary>
    private const double AchromaticChroma = 12;

    /// <summary>How far the background ring reaches beyond the box, as a fraction of its size.</summary>
    private const double RingFraction = 0.2;

    /// <summary>A surrounding color counts as background once it covers this share of the ring.</summary>
    private const double BackgroundMinShare = 0.15;

    /// <summary>A cluster this close (Lab ΔE) to a background color is taken to be background.</summary>
    private const double BackgroundDistance = 15;

    /// <summary>A non-background cluster must hold this share of the region to win over the biggest
    /// one — below it the object is most likely the same color as its surroundings.</summary>
    private const double ObjectMinShare = 0.2;

    public static ObjectColorResult Classify(FramePixels frame, PixelRect box, string category)
    {
        box = box.ClampTo(frame.Width, frame.Height);
        if (box.Width < MinRegionPixels || box.Height < MinRegionPixels) return new(null, null, null);
        var background = BackgroundColors(frame, box);

        switch (category)
        {
            case "Human":
            {
                // Head is roughly the top sixth; the shirt band sits below it, pants below the waist.
                var upper = ClassifyRegion(frame, box.Part(0.25, 0.18, 0.75, 0.50), vehicle: false, background);
                var lower = ClassifyRegion(frame, box.Part(0.30, 0.55, 0.70, 0.88), vehicle: false, background);
                return new(upper, upper, lower);
            }
            case "Vehicle":
                // Skip the roof/sky edge and the tyres/road at the bottom.
                return new(ClassifyRegion(frame, box.Part(0.15, 0.25, 0.85, 0.80), vehicle: true, background), null, null);
            default:
                return new(ClassifyRegion(frame, box.Part(0.20, 0.20, 0.80, 0.80), vehicle: false, background), null, null);
        }
    }

    /// <summary>The main colors of a ring just outside <paramref name="box"/> — what the scene
    /// behind the object looks like. Empty when the box fills the frame and there's no ring.</summary>
    public static List<(double L, double A, double B)> BackgroundColors(FramePixels frame, PixelRect box)
    {
        var padX = Math.Max(4, (int)Math.Round(box.Width * RingFraction));
        var padY = Math.Max(4, (int)Math.Round(box.Height * RingFraction));
        var outer = new PixelRect(box.Left - padX, box.Top - padY, box.Right + padX, box.Bottom + padY)
            .ClampTo(frame.Width, frame.Height);

        const int grid = 30;
        var samples = new List<(double L, double A, double B)>();
        for (var gy = 0; gy < grid; gy++)
        {
            var y = outer.Top + (int)((gy + 0.5) * outer.Height / grid);
            for (var gx = 0; gx < grid; gx++)
            {
                var x = outer.Left + (int)((gx + 0.5) * outer.Width / grid);
                if (x >= box.Left && x < box.Right && y >= box.Top && y < box.Bottom) continue;
                var (r, g, b) = frame.Rgb(x, y);
                samples.Add(ToLab(r, g, b));
            }
        }
        if (samples.Count < 12) return [];

        var (centres, counts) = KMeans([.. samples]);
        return [.. centres.Where((_, i) => counts[i] >= samples.Count * BackgroundMinShare)];
    }

    /// <summary>True when nearly every pixel on a sparse grid over <paramref name="content"/> has no
    /// chroma — the camera has dropped its color filter (IR night mode) or sends grayscale.</summary>
    public static bool IsMonochrome(FramePixels frame, PixelRect content)
    {
        content = content.ClampTo(frame.Width, frame.Height);
        if (content.Width < 2 || content.Height < 2) return false;
        const int grid = 24;
        int gray = 0, total = 0;
        for (var gy = 0; gy < grid; gy++)
        {
            var y = content.Top + (int)((gy + 0.5) * content.Height / grid);
            for (var gx = 0; gx < grid; gx++)
            {
                var x = content.Left + (int)((gx + 0.5) * content.Width / grid);
                var (r, g, b) = frame.Rgb(x, y);
                var (_, a, bb) = ToLab(r, g, b);
                total++;
                if (a * a + bb * bb < 4 * 4) gray++;
            }
        }
        return gray >= total * 0.97;
    }

    /// <summary>Names the dominant color of one region, or null when it's too small. Clusters
    /// matching a <paramref name="background"/> color are passed over when another holds a fair share
    /// of the region.</summary>
    public static string? ClassifyRegion(FramePixels frame, PixelRect region, bool vehicle,
        IReadOnlyList<(double L, double A, double B)>? background = null)
    {
        region = region.ClampTo(frame.Width, frame.Height);
        if (region.Width < 2 || region.Height < 2) return null;

        var nx = Math.Min(MaxSamplesPerSide, region.Width);
        var ny = Math.Min(MaxSamplesPerSide, region.Height);
        var samples = new (double L, double A, double B)[nx * ny];
        var n = 0;
        for (var sy = 0; sy < ny; sy++)
        {
            var y = region.Top + (int)((sy + 0.5) * region.Height / ny);
            for (var sx = 0; sx < nx; sx++)
            {
                var x = region.Left + (int)((sx + 0.5) * region.Width / nx);
                var (r, g, b) = frame.Rgb(x, y);
                samples[n++] = ToLab(r, g, b);
            }
        }

        var (centres, counts) = KMeans(samples);
        var order = Enumerable.Range(0, centres.Length).OrderByDescending(i => counts[i]).ToArray();
        if (background is { Count: > 0 })
        {
            var foreground = order.Where(i => counts[i] >= samples.Length * ObjectMinShare
                && !background.Any(bg => Distance(centres[i], bg) < BackgroundDistance)).ToArray();
            if (foreground.Length > 0) order = foreground;
        }
        var pick = order[0];
        // Windows, tyres and shadows read as near-black. When a real color is nearly as common, the
        // dark cluster is the shadow, not the object.
        if (centres[pick].L < 25 && order.Length > 1 && counts[order[1]] >= counts[pick] * 0.5
            && Chroma(centres[order[1]]) >= AchromaticChroma)
            pick = order[1];

        var c = centres[pick];
        return Name(c.L, c.A, c.B, vehicle);
    }

    /// <summary>Maps one Lab color onto the fixed name list. Hue boundaries come from where the
    /// sRGB reference colors land in Lab: red ≈ 40°, orange ≈ 65°, yellow ≈ 100°, green ≈ 135°,
    /// cyan ≈ 195°, blue ≈ 305°, purple ≈ 328°, pink ≈ 350°/8°.</summary>
    public static string Name(double l, double a, double b, bool vehicle)
    {
        var chroma = Math.Sqrt(a * a + b * b);
        if (chroma < AchromaticChroma)
        {
            if (l < 25) return "black";
            if (l < 55) return "gray";
            if (l < 80) return vehicle ? "silver" : "gray";
            return "white";
        }

        var hue = Math.Atan2(b, a) * 180 / Math.PI;
        if (hue < 0) hue += 360;

        if (hue >= 340 || hue < 15) return l > 60 ? "pink" : "red";
        if (hue < 50) return l < 30 && chroma < 30 ? "brown" : "red";
        if (hue < 85) return l < 55 || chroma < 35 ? "brown" : "orange";
        if (hue < 110) return l < 40 ? "green" : "yellow";
        if (hue < 190) return "green";
        if (hue < 312) return "blue";
        return l > 65 ? "pink" : "purple";
    }

    private static double Chroma((double L, double A, double B) c) => Math.Sqrt(c.A * c.A + c.B * c.B);

    private static double Distance((double L, double A, double B) p, (double L, double A, double B) q)
    {
        double dl = p.L - q.L, da = p.A - q.A, db = p.B - q.B;
        return Math.Sqrt(dl * dl + da * da + db * db);
    }

    /// <summary>Deterministic k-means: seeded from the darkest, brightest and most saturated sample
    /// so the same crop always gives the same answer.</summary>
    private static ((double L, double A, double B)[] Centres, int[] Counts) KMeans((double L, double A, double B)[] samples)
    {
        var k = Math.Min(Clusters, samples.Length);
        var centres = new (double L, double A, double B)[k];
        var seeds = new[]
        {
            samples.MinBy(s => s.L),
            samples.MaxBy(s => s.L),
            samples.MaxBy(Chroma),
        };
        for (var i = 0; i < k; i++) centres[i] = seeds[i];

        var assign = new int[samples.Length];
        var counts = new int[k];
        for (var iter = 0; iter < Iterations; iter++)
        {
            Array.Clear(counts);
            var sums = new (double L, double A, double B)[k];
            for (var s = 0; s < samples.Length; s++)
            {
                var best = 0;
                var bestD = double.MaxValue;
                for (var c = 0; c < k; c++)
                {
                    var dl = samples[s].L - centres[c].L;
                    var da = samples[s].A - centres[c].A;
                    var db = samples[s].B - centres[c].B;
                    var d = dl * dl + da * da + db * db;
                    if (d < bestD) { bestD = d; best = c; }
                }
                assign[s] = best;
                counts[best]++;
                sums[best] = (sums[best].L + samples[s].L, sums[best].A + samples[s].A, sums[best].B + samples[s].B);
            }
            for (var c = 0; c < k; c++)
            {
                if (counts[c] > 0) centres[c] = (sums[c].L / counts[c], sums[c].A / counts[c], sums[c].B / counts[c]);
            }
        }
        return (centres, counts);
    }

    /// <summary>sRGB (0-255) → CIE Lab (D65).</summary>
    public static (double L, double A, double B) ToLab(byte r, byte g, byte b)
    {
        static double Lin(byte v)
        {
            var c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        double rl = Lin(r), gl = Lin(g), bl = Lin(b);
        var x = (0.4124 * rl + 0.3576 * gl + 0.1805 * bl) / 0.95047;
        var y = 0.2126 * rl + 0.7152 * gl + 0.0722 * bl;
        var z = (0.0193 * rl + 0.1192 * gl + 0.9505 * bl) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        double fx = F(x), fy = F(y), fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}
