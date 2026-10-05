using System.Globalization;

namespace LarisVMS.Vision.Inference;

/// <summary>
/// Decides whether a GPU-native batched Slice graph's per-slot outputs agree with N separate
/// single-tile runs — <see cref="GenericOnnxEngine"/>'s numeric self-check, as a pure function so it
/// can be tested on plain arrays.
///
/// The two runs are never bit-identical: the batched graph preprocesses in float on the GPU while the
/// single-tile path rounds every pixel to a byte, and the two are separate TensorRT engines with their
/// own FP16 kernels. Outputs are often in input-pixel units (box corners) and sorted by score, so that
/// noise shows up as fractions of a pixel and as near-tied rows trading places. An element-wise,
/// positional comparison rejected well-behaved models over exactly that. What this check has to catch
/// is gross: an export whose batched graph is shape-valid but silently wrong (the frozen batch index
/// documented on <see cref="GenericOnnxEngine"/> — every slot carrying one tile's content).
///
/// So each slot's output is compared as a set of rows (its last dimension is the row width), by the
/// mean distance from each row to its nearest counterpart, relative to the output's own magnitude:
/// <list type="bullet">
/// <item>Closeness: slot i must be within <see cref="MaxRelativeDistance"/> of single-tile run i.</item>
/// <item>Identity: slot i must be clearly nearer to tile i than to any other tile — the frozen-index
/// failure passes closeness for one slot and fails this for the rest. Can't be run when the
/// single-tile outputs are too alike to tell apart; that is reported, and the caller must then treat
/// the graph as unverified — closeness alone once passed a graph that only ever filled slot 0.</item>
/// </list>
/// </summary>
internal static class SlicedOutputComparison
{
    /// <summary>Distance between matched outputs, as a fraction of the output's RMS row norm.</summary>
    internal const double MaxRelativeDistance = 0.05;

    /// <summary>Slot i's distance to tile i must be at most this fraction of its distance to the
    /// nearest other tile.</summary>
    internal const double IdentityMargin = 0.5;

    // Nearest-row matching is quadratic in the row count. Outputs whose rows are a fixed layout rather
    // than a ranked list (raw anchor grids like [84, 8400]) can be very wide; above this many
    // multiply-adds the rows are compared positionally instead, which is correct for them anyway.
    private const long MaxNearestRowWork = 50_000_000;

    /// <summary>Null when the batched output is consistent with the single-tile outputs; otherwise a
    /// one-line reason. <paramref name="identitySkipped"/> is set when the single-tile outputs were too
    /// similar to each other for the identity test to mean anything.</summary>
    /// <param name="batchedSlots">Slot i of the batched output, one array per slice.</param>
    /// <param name="singleSlots">Single-tile run i's output, one array per slice.</param>
    /// <param name="rowWidth">Size of the output's last dimension.</param>
    internal static string? Compare(string outputName, IReadOnlyList<float[]> batchedSlots,
        IReadOnlyList<float[]> singleSlots, int rowWidth, out bool identitySkipped)
    {
        identitySkipped = false;
        var n = singleSlots.Count;
        if (batchedSlots.Count != n)
            return $"output '{outputName}' has {batchedSlots.Count} batched slot(s) for {n} tile(s)";
        if (rowWidth <= 0) rowWidth = 1;

        // One scale for the whole output: per-slot scales would let an all-zero tile make any
        // difference look infinite.
        var scale = 0.0;
        foreach (var s in singleSlots) scale = Math.Max(scale, RmsRowNorm(s, rowWidth));
        scale = Math.Max(scale, 1e-6);

        for (var i = 0; i < n; i++)
        {
            var own = Distance(batchedSlots[i], singleSlots[i], rowWidth) / scale;
            if (own > MaxRelativeDistance)
                return string.Create(CultureInfo.InvariantCulture,
                    $"output '{outputName}' slot {i} is {own:P1} away from its own single-tile run (limit {MaxRelativeDistance:P0})");
        }

        if (n < 2) return null;

        var minBetweenTiles = double.MaxValue;
        for (var j = 0; j < n; j++)
            for (var k = j + 1; k < n; k++)
                minBetweenTiles = Math.Min(minBetweenTiles, Distance(singleSlots[j], singleSlots[k], rowWidth) / scale);
        if (minBetweenTiles <= MaxRelativeDistance)
        {
            identitySkipped = true;
            return null;
        }

        for (var i = 0; i < n; i++)
        {
            var own = Distance(batchedSlots[i], singleSlots[i], rowWidth);
            var nearestOther = double.MaxValue;
            var nearestOtherIndex = -1;
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                var d = Distance(batchedSlots[i], singleSlots[j], rowWidth);
                if (d < nearestOther) { nearestOther = d; nearestOtherIndex = j; }
            }

            if (own > IdentityMargin * nearestOther)
                return $"output '{outputName}' slot {i} matches tile {nearestOtherIndex} at least as well as its own tile " +
                    "(every slot carrying one tile's content is the frozen-batch-index export bug)";
        }

        return null;
    }

    /// <summary>Symmetric mean nearest-row distance between two same-shaped outputs.</summary>
    internal static double Distance(float[] a, float[] b, int rowWidth)
    {
        var rows = Math.Min(a.Length, b.Length) / rowWidth;
        if (rows == 0) return 0;

        if ((long)rows * rows * rowWidth > MaxNearestRowWork)
        {
            var sum = 0.0;
            for (var r = 0; r < rows; r++) sum += RowDistance(a, r, b, r, rowWidth);
            return sum / rows;
        }

        return (MeanNearest(a, b, rows, rowWidth) + MeanNearest(b, a, rows, rowWidth)) / 2;
    }

    private static double MeanNearest(float[] from, float[] to, int rows, int w)
    {
        var sum = 0.0;
        for (var r = 0; r < rows; r++)
        {
            var best = double.MaxValue;
            for (var t = 0; t < rows && best > 0; t++)
                best = Math.Min(best, RowDistance(from, r, to, t, w));
            sum += best;
        }
        return sum / rows;
    }

    private static double RowDistance(float[] a, int ra, float[] b, int rb, int w)
    {
        var oa = ra * w;
        var ob = rb * w;
        var sum = 0.0;
        for (var c = 0; c < w; c++)
        {
            var d = (double)a[oa + c] - b[ob + c];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }

    private static double RmsRowNorm(float[] values, int w)
    {
        var rows = values.Length / w;
        if (rows == 0) return 0;
        var sum = 0.0;
        for (var i = 0; i < rows * w; i++) sum += (double)values[i] * values[i];
        return Math.Sqrt(sum / rows);
    }
}
