namespace LarisVMS.Vision.Inference;

/// <summary>
/// Detection/hardware-acceleration overhaul, pass 3b: pure tile-placement geometry for
/// motion-guided native-scale re-detection (SAHI-style) — where to cut native-scale tiles out of a
/// full-resolution Main-stream frame, and how to map a box found inside one back into that frame's
/// own coordinates. No I/O, no ONNX, no ffmpeg — everything here is plain arithmetic, unit-tested
/// directly.
/// </summary>
public static class TileLayout
{
    /// <summary>One trigger's own normalized (0-1) top-left bounding box (matches every other
    /// normalized box representation in this codebase, e.g. VisionLiveDetectionBox) — a ByteTrack
    /// centroid's own reported box, used both to place a tile around its center and to decide
    /// whether the object is already too large for a native tile to be worth cutting.</summary>
    public readonly record struct TriggerPoint(double XNorm, double YNorm, double WNorm, double HNorm)
    {
        public double CenterXNorm => XNorm + (WNorm / 2);
        public double CenterYNorm => YNorm + (HNorm / 2);
    }

    /// <summary>One native-scale tile's own rectangle in full-frame pixel coordinates.</summary>
    public readonly record struct Tile(int X, int Y, int Width, int Height);

    /// <summary>Places up to <paramref name="maxTiles"/> native <paramref name="tileSize"/>x
    /// <paramref name="tileSize"/> tiles in <paramref name="frameWidth"/>x<paramref
    /// name="frameHeight"/> pixel space, one per trigger centroid — after: (1) dropping any trigger
    /// whose own box already exceeds the tile size in either dimension (that object is large enough
    /// for the whole-frame pass to catch whole; a downscaled "native-scale" tile for it would be
    /// neither native nor whole-frame, just a worse duplicate of both), and (2) merging trigger
    /// points closer together than half a tile (their tiles would mostly overlap anyway). Each
    /// placed tile is centered on its trigger, then clamped fully inside the frame.</summary>
    public static List<Tile> PlaceTiles(IReadOnlyList<TriggerPoint> triggers, int tileSize,
        int frameWidth, int frameHeight, int maxTiles = 4)
    {
        // A frame smaller than the tile itself can't produce a tile at the model's fixed network
        // input size without stretching it — and the whole-frame letterboxed pass already covers a
        // frame this small perfectly well, so native tiling adds nothing here anyway. Not a real
        // scenario for an actual Main stream (always the high-resolution one), but this keeps
        // PlaceTiles from ever handing back a tile DFineEngine.DetectBatch's fixed-network-size
        // validation would reject.
        if (frameWidth < tileSize || frameHeight < tileSize) return [];

        var candidates = new List<(double CenterX, double CenterY)>();
        foreach (var trigger in triggers)
        {
            var boxWidthPx = trigger.WNorm * frameWidth;
            var boxHeightPx = trigger.HNorm * frameHeight;
            if (boxWidthPx > tileSize || boxHeightPx > tileSize) continue; // whole-frame pass already covers this one

            candidates.Add((trigger.CenterXNorm * frameWidth, trigger.CenterYNorm * frameHeight));
        }

        var merged = MergeClose(candidates, tileSize / 2.0);

        var tiles = new List<Tile>(Math.Min(merged.Count, maxTiles));
        foreach (var (centerX, centerY) in merged.Take(maxTiles))
        {
            var x = (int)Math.Round(centerX - (tileSize / 2.0));
            var y = (int)Math.Round(centerY - (tileSize / 2.0));

            // Clamp fully inside the frame — a centroid near the edge would otherwise ask for a
            // region ffmpeg/SkiaSharp's own crop rejects outright, the same reasoning
            // SnapshotImageCapture.ComputeCropRect's own frame-bounds clamp already documents.
            var width = Math.Min(tileSize, frameWidth);
            var height = Math.Min(tileSize, frameHeight);
            x = Math.Clamp(x, 0, Math.Max(0, frameWidth - width));
            y = Math.Clamp(y, 0, Math.Max(0, frameHeight - height));

            tiles.Add(new Tile(x, y, width, height));
        }

        return tiles;
    }

    /// <summary>Merges centroids closer than <paramref name="mergeDistance"/> pixels apart into one
    /// (their average) — greedy, order-independent for the test cases this needs to satisfy (no
    /// pathological clustering guarantees beyond "two points within half a tile of each other don't
    /// each get their own near-identical tile").</summary>
    private static List<(double CenterX, double CenterY)> MergeClose(
        List<(double CenterX, double CenterY)> points, double mergeDistance)
    {
        var remaining = new List<(double CenterX, double CenterY)>(points);
        var merged = new List<(double CenterX, double CenterY)>();

        while (remaining.Count > 0)
        {
            var anchor = remaining[0];
            var cluster = new List<(double CenterX, double CenterY)> { anchor };
            remaining.RemoveAt(0);

            remaining.RemoveAll(p =>
            {
                var dx = p.CenterX - anchor.CenterX;
                var dy = p.CenterY - anchor.CenterY;
                var isClose = Math.Sqrt((dx * dx) + (dy * dy)) < mergeDistance;
                if (isClose) cluster.Add(p);
                return isClose;
            });

            merged.Add((cluster.Average(p => p.CenterX), cluster.Average(p => p.CenterY)));
        }

        return merged;
    }

    /// <summary>Maps a box reported in one tile's own local pixel space (0,0 at the tile's own
    /// top-left) back into full-frame pixel coordinates — pure offset addition, and per the original
    /// plan's own callout, the single most important thing in this pass to get right by test rather
    /// than by eyeballing it.</summary>
    public static (int X, int Y, int Width, int Height) MapTileBoxToFrame(Tile tile,
        int boxXInTile, int boxYInTile, int boxWidth, int boxHeight)
        => (tile.X + boxXInTile, tile.Y + boxYInTile, boxWidth, boxHeight);
}
