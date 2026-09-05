namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 4: pure geometry for <see cref="LarisVMS.Core.Enums.AspectMode.Slice"/> —
/// where to cut a camera's own aspect-ratio-preserving capture frame into overlapping
/// <c>networkSize</c>×<c>networkSize</c> squares, and how to map a box a detector found inside one
/// slice back into the camera's own normalized source-frame coordinates. No I/O, no ONNX, no
/// ffmpeg — everything here is plain arithmetic, unit-tested directly, the same shape the deleted
/// <c>TileLayout</c> (motion-guided native-scale tiling, removed) already established for this
/// codebase.
///
/// The scale is uniform and aspect-preserving (short edge → <c>networkSize</c>, long edge scaled by
/// the same factor) with no padding at all, unlike <see cref="InferenceProfile"/>'s Letterbox mode —
/// there is no content rectangle to exclude and no black bars to route around. That is what makes
/// <see cref="MapSliceBoxToSource"/> a plain offset + uniform scale, the cheap exact case, rather
/// than needing a full letterbox inverse transform.
/// </summary>
public sealed class SliceLayout
{
    /// <summary>One slice's own rectangle in capture-pixel space — always exactly
    /// <c>networkSize</c>×<c>networkSize</c>.</summary>
    public readonly record struct Tile(int X, int Y, int Width, int Height);

    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int NetworkSize { get; }

    /// <summary>The ffmpeg capture buffer's own dimensions — the camera's short edge scaled to
    /// exactly <see cref="NetworkSize"/>, the long edge scaled by the same factor and even-aligned
    /// (never below <see cref="NetworkSize"/>). Landscape source (<see cref="SourceWidth"/> &gt;=
    /// <see cref="SourceHeight"/>): <see cref="CaptureHeight"/> == <see cref="NetworkSize"/> and the
    /// long edge is <see cref="CaptureWidth"/>; portrait is the mirror image.</summary>
    public int CaptureWidth { get; }
    public int CaptureHeight { get; }

    /// <summary>Every slice's own rectangle, in capture-pixel order along the long edge (left-to-right
    /// for landscape, top-to-bottom for portrait). Always at least 2 (a camera whose long edge is no
    /// bigger than the short edge — already square or near it — still gets 2 overlapping slices
    /// rather than degenerating to 1, since 1 would just be the discarded old Stretch/Letterbox
    /// behavior in disguise, with none of the "more pixels on target" benefit slicing exists for).</summary>
    public IReadOnlyList<Tile> Slices { get; }

    private readonly bool _landscape;

    private SliceLayout(int sourceWidth, int sourceHeight, int networkSize, int captureWidth, int captureHeight,
        IReadOnlyList<Tile> slices, bool landscape)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        NetworkSize = networkSize;
        CaptureWidth = captureWidth;
        CaptureHeight = captureHeight;
        Slices = slices;
        _landscape = landscape;
    }

    public static SliceLayout Create(int sourceWidth, int sourceHeight, int networkSize = InferenceProfile.DefaultNetworkSize)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Source dimensions must be positive.");
        if (networkSize <= 0 || networkSize % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(networkSize), "Network size must be a positive even number.");

        var landscape = sourceWidth >= sourceHeight;
        var shortSource = landscape ? sourceHeight : sourceWidth;
        var longSource = landscape ? sourceWidth : sourceHeight;

        var scale = (double)networkSize / shortSource;
        var longScaled = (int)Math.Round(longSource * scale);
        // Even (4:2:0 chroma subsampling on the ffmpeg-produced nv12 capture buffer — same convention
        // InferenceProfile.CreateLetterbox already uses) and never below networkSize, so N below is
        // always >= 1 and every slice fits inside the capture buffer without clamping.
        longScaled = Math.Max(networkSize, longScaled - longScaled % 2);

        var captureWidth = landscape ? longScaled : networkSize;
        var captureHeight = landscape ? networkSize : longScaled;

        // Always at least 2 — see Slices' own doc comment for why a near-square camera still gets a
        // real second slice rather than collapsing to 1.
        var n = Math.Max(2, (int)Math.Ceiling((double)longScaled / networkSize));
        var maxOrigin = longScaled - networkSize;

        var origins = new int[n];
        for (var i = 0; i < n; i++)
        {
            // Evenly spread so overlap is equal between neighbours; first at 0 and last flush to the
            // far edge are set exactly (not left to rounding) since maxOrigin is itself always even
            // (both longScaled and networkSize are even) — no fractional-pixel drift at the edges.
            origins[i] = i == 0 ? 0
                : i == n - 1 ? maxOrigin
                : (int)Math.Round(maxOrigin * i / (double)(n - 1) / 2.0) * 2;
        }

        var slices = new List<Tile>(n);
        foreach (var origin in origins)
        {
            slices.Add(landscape ? new Tile(origin, 0, networkSize, networkSize) : new Tile(0, origin, networkSize, networkSize));
        }

        return new SliceLayout(sourceWidth, sourceHeight, networkSize, captureWidth, captureHeight, slices, landscape);
    }

    /// <summary>Maps a box reported in slice <paramref name="sliceIndex"/>'s own normalized (0-1)
    /// local space — exactly what a detector sees, since a slice is already the model's full
    /// <see cref="NetworkSize"/>×<see cref="NetworkSize"/> input with no further transform — to
    /// normalized (0-1) *source*-frame coordinates, the same convention
    /// <see cref="InferenceProfile.MapBoxToSource"/> and every other box normalization in this
    /// codebase uses. A plain offset + uniform scale: the capture buffer is a uniform aspect-
    /// preserving scale of the source frame with no padding at all, unlike Letterbox's inverse
    /// transform. Not clamped to [0,1] — same reasoning as <see cref="InferenceProfile.MapBoxToSource"/>'s
    /// own doc comment (an object exiting past the camera's own edge can legitimately land slightly
    /// outside); the caller clamps.</summary>
    public (double X0, double Y0, double X1, double Y1) MapSliceBoxToSource(int sliceIndex, double x0, double y0, double x1, double y1)
    {
        var tile = Slices[sliceIndex];

        // Slice-local normalized -> capture-pixel.
        var capX0 = tile.X + x0 * tile.Width;
        var capY0 = tile.Y + y0 * tile.Height;
        var capX1 = tile.X + x1 * tile.Width;
        var capY1 = tile.Y + y1 * tile.Height;

        // Capture-pixel -> source-normalized. No pad/offset to undo — CaptureWidth/Height is a
        // uniform scale of SourceWidth/Height (see this class's own doc comment).
        return (capX0 / CaptureWidth, capY0 / CaptureHeight, capX1 / CaptureWidth, capY1 / CaptureHeight);
    }

    /// <summary>Each slice's own origin along the long edge, in capture pixels (X for landscape, Y for
    /// portrait) — the numbers <see cref="Slices"/> is built from, exposed for logging and for
    /// reasoning about the seams between neighbours.</summary>
    public IReadOnlyList<int> Origins => [.. Slices.Select(t => _landscape ? t.X : t.Y)];

    /// <summary>The smallest overlap, in capture pixels, between any two neighbouring slices — the
    /// widest an object can be and still be seen whole by at least one slice. Worth logging and
    /// watching: an object wider than this is clipped in *every* slice that contains part of it, and
    /// can only be recovered by <see cref="SliceMerge"/>'s seam rule. Note this can legitimately reach
    /// 0 today, when the scaled long edge is an exact multiple of <see cref="NetworkSize"/> — two
    /// butted-up slices with no shared band at all, where seam fragments cannot overlap and so cannot
    /// merge.</summary>
    public int MinOverlap
    {
        get
        {
            var origins = Origins;
            var min = NetworkSize;
            for (var i = 1; i < origins.Count; i++)
            {
                min = Math.Min(min, NetworkSize - (origins[i] - origins[i - 1]));
            }
            return min;
        }
    }

    /// <summary>True when the long edge runs left-to-right (a landscape or square source) — the axis
    /// <see cref="Slices"/> are spread along. Exposed for callers building the accelerator-side slice
    /// graph, which needs to know which tensor axis (width vs height) to cut on.</summary>
    public bool IsLandscape => _landscape;
}
