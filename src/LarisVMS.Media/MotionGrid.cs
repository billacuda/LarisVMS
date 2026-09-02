namespace LarisVMS.Media;

/// <summary>
/// Detection/hardware-acceleration overhaul pass 3c-2: Grid mode's own mask representation and
/// per-cell scoring — a sibling to ZoneRasterizer/MotionDetector, not a replacement for either.
/// Rasterize produces the same bool[width*height] mask shape ZoneRasterizer's own polygon output
/// does, so MotionSession/MotionDetector.Score need no knowledge of which mode built a given mask —
/// one masking mechanism with two editors, rather than two mechanisms that can disagree.
/// </summary>
public static class MotionGrid
{
    /// <summary>Bytes needed to hold gridSize*gridSize bits, row-major, cell (row,col) at bit
    /// row*gridSize+col.</summary>
    public static int ByteCount(int gridSize) => (gridSize * gridSize + 7) / 8;

    /// <summary>An all-zero mask (nothing masked — watch the whole frame) at the given grid size.</summary>
    public static string EmptyMask(int gridSize) => Convert.ToBase64String(new byte[ByteCount(gridSize)]);

    /// <summary>Decodes a stored mask, or returns an all-zero byte array of the right size for a
    /// null/empty/corrupted/undersized one — same "fail toward watching, not toward silently going
    /// blind" default Polygon mode already has (zero Ignore zones watches everything). Undersized
    /// specifically covers a grid size that just increased with no re-save yet.</summary>
    private static byte[] DecodeOrEmpty(string? maskBase64, int gridSize)
    {
        var needed = ByteCount(gridSize);
        if (string.IsNullOrEmpty(maskBase64)) return new byte[needed];
        byte[] decoded;
        try { decoded = Convert.FromBase64String(maskBase64); }
        catch (FormatException) { return new byte[needed]; }
        if (decoded.Length == needed) return decoded;
        var resized = new byte[needed];
        Array.Copy(decoded, resized, Math.Min(decoded.Length, needed));
        return resized;
    }

    private static bool IsMasked(byte[] bytes, int cellIndex)
    {
        var byteIndex = cellIndex / 8;
        if (byteIndex >= bytes.Length) return false;
        return (bytes[byteIndex] & (1 << (cellIndex % 8))) != 0;
    }

    /// <summary>True if the given cell (row-major index) is currently masked.</summary>
    public static bool IsCellMasked(string? maskBase64, int gridSize, int cellIndex) =>
        IsMasked(DecodeOrEmpty(maskBase64, gridSize), cellIndex);

    /// <summary>Returns a new mask with one cell's bit set/cleared — the editor's own click-to-toggle
    /// action. A mask shorter than this grid size needs (a size that just increased, or nothing saved
    /// yet) is grown first, same as DecodeOrEmpty.</summary>
    public static string SetCellMasked(string? maskBase64, int gridSize, int cellIndex, bool masked)
    {
        var bytes = DecodeOrEmpty(maskBase64, gridSize);
        var byteIndex = cellIndex / 8;
        var bit = (byte)(1 << (cellIndex % 8));
        if (masked) bytes[byteIndex] |= bit; else bytes[byteIndex] &= (byte)~bit;
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Rasterizes a grid mask to a width*height boolean mask at the motion pipeline's fixed
    /// working resolution — mask[i] is true where pixel i is *watched* (its cell is not masked),
    /// exactly the polarity ZoneRasterizer.Rasterize's own polygon output has (true = counts toward
    /// motion). Each pixel maps to exactly one cell by simple proportional division — cells divide
    /// the frame evenly, so they're non-square whenever width and height aren't both exact multiples
    /// of gridSize. Built once per reconcile (or mask change), not per frame, same as
    /// ZoneRasterizer.Rasterize.</summary>
    public static bool[] Rasterize(string? maskBase64, int gridSize, int width, int height)
    {
        var maskBytes = DecodeOrEmpty(maskBase64, gridSize);
        var mask = new bool[width * height];
        for (var y = 0; y < height; y++)
        {
            var cellRow = y * gridSize / height;
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var cellCol = x * gridSize / width;
                mask[rowOffset + x] = !IsMasked(maskBytes, cellRow * gridSize + cellCol);
            }
        }
        return mask;
    }

    /// <summary>True if at least one cell is not masked — i.e. Grid mode has anything left to watch
    /// at all. A cheap cell-count-sized check (gridSize*gridSize iterations), not a full
    /// width*height frame rasterization — used purely to decide whether to run a session at all, the
    /// Grid-mode equivalent of Polygon mode's "are there any enabled ServerMotion zones" check.</summary>
    public static bool HasAnyUnmaskedCell(string? maskBase64, int gridSize)
    {
        var bytes = DecodeOrEmpty(maskBase64, gridSize);
        var cellCount = gridSize * gridSize;
        for (var i = 0; i < cellCount; i++)
        {
            if (!IsMasked(bytes, i)) return true;
        }
        return false;
    }

    /// <summary>Every cell's own motion score in one pass over the frame — the live per-cell feed
    /// pass 3c-2's editor draws, independent of the single whole-region score MotionSession's own
    /// hysteresis instance drives for span reporting (see NodeWorker.ReconcileMotion's own doc
    /// comment for how the two combine). Deliberately not gridSize*gridSize separate
    /// MotionDetector.Score calls — each of those would rescan the *entire* frame just to find its
    /// own cell's handful of pixels, an O(gridSize^2 * width * height) cost instead of this method's
    /// single O(width*height) pass. A masked cell's own score is still computed here — a viewer
    /// tuning the mask wants to see what a cell *would* be doing before excluding it; only
    /// Rasterize's output, not this, decides what actually counts toward the reported span.</summary>
    public static double[] ScoreCells(ReadOnlySpan<byte> previousFrame, ReadOnlySpan<byte> currentFrame,
        int gridSize, int width, int height, byte pixelDeltaThreshold)
    {
        if (previousFrame.Length != currentFrame.Length || previousFrame.Length != width * height)
            throw new ArgumentException("previousFrame and currentFrame must both be exactly width*height bytes.");

        var cellCount = gridSize * gridSize;
        var totalCounts = new int[cellCount];
        var changedCounts = new int[cellCount];

        for (var y = 0; y < height; y++)
        {
            var cellRow = y * gridSize / height;
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var cellIndex = cellRow * gridSize + (x * gridSize / width);
                totalCounts[cellIndex]++;
                var i = rowOffset + x;
                if (Math.Abs(previousFrame[i] - currentFrame[i]) > pixelDeltaThreshold) changedCounts[cellIndex]++;
            }
        }

        var scores = new double[cellCount];
        for (var c = 0; c < cellCount; c++)
            scores[c] = totalCounts[c] == 0 ? 0.0 : (double)changedCounts[c] / totalCounts[c];
        return scores;
    }
}
