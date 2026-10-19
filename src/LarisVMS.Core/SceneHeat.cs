using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace LarisVMS.Core;

/// <summary>
/// The pieces scene heatmaps share between the vision service, the node, the server and its tests:
/// an object's path as compact text, a grid of counts stored as only its non-zero cells, and drawing
/// a path into a grid. A heatmap cell's value is how many objects passed through that spot (each
/// object counts once per cell), or for motion, how many analysed frames had motion there.
/// </summary>
public static class SceneHeat
{
    /// <summary>Objects heat grid width; rows follow the camera's aspect ratio.</summary>
    public const int ObjectColumns = 64;

    /// <summary>Rows for a <see cref="ObjectColumns"/>-wide grid on a <paramref name="width"/> x
    /// <paramref name="height"/> picture (16:9 when unknown), kept within 8-64.</summary>
    public static int RowsFor(int? width, int? height) =>
        width is > 0 && height is > 0
            ? Math.Clamp((int)Math.Round(ObjectColumns * (double)height.Value / width.Value), 8, 64)
            : 36;

    // ── Paths ─────────────────────────────────────────────────────────────

    /// <summary>"x,y;x,y;…" with 3 decimals, normalized 0-1 — about 12 bytes a point.</summary>
    public static string FormatPath(IEnumerable<(float X, float Y)> points)
    {
        var sb = new StringBuilder();
        foreach (var (x, y) in points)
        {
            if (sb.Length > 0) sb.Append(';');
            sb.Append(Math.Clamp(x, 0f, 1f).ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(Math.Clamp(y, 0f, 1f).ToString("0.###", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>The points of <see cref="FormatPath"/>'s text; malformed pairs are skipped.</summary>
    public static List<(float X, float Y)> ParsePath(string? text)
    {
        var points = new List<(float X, float Y)>();
        if (string.IsNullOrEmpty(text)) return points;
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = pair.IndexOf(',');
            if (comma <= 0) continue;
            if (float.TryParse(pair.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && float.TryParse(pair.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                points.Add((Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f)));
        }
        return points;
    }

    /// <summary>Every cell of a <paramref name="cols"/> x <paramref name="rows"/> grid the path passes
    /// through, each once — the straight line between consecutive points is walked in steps of a
    /// fraction of a cell, so a fast object's sparse points still leave a continuous trail.</summary>
    public static HashSet<int> PathCells(IReadOnlyList<(float X, float Y)> path, int cols, int rows)
    {
        var cells = new HashSet<int>();
        if (path.Count == 0 || cols <= 0 || rows <= 0) return cells;
        int Cell(float x, float y) =>
            Math.Min(rows - 1, (int)(y * rows)) * cols + Math.Min(cols - 1, (int)(x * cols));

        cells.Add(Cell(path[0].X, path[0].Y));
        for (var i = 1; i < path.Count; i++)
        {
            var (ax, ay) = path[i - 1];
            var (bx, by) = path[i];
            var spanCells = Math.Max(Math.Abs(bx - ax) * cols, Math.Abs(by - ay) * rows);
            var steps = Math.Max(1, (int)Math.Ceiling(spanCells * 3));
            for (var s = 1; s <= steps; s++)
            {
                var f = s / (float)steps;
                cells.Add(Cell(ax + (bx - ax) * f, ay + (by - ay) * f));
            }
        }
        return cells;
    }

    // ── Sparse grids ──────────────────────────────────────────────────────

    /// <summary>Only the non-zero cells, as (uint16 index, uint32 count) pairs — a quiet hour costs a
    /// few bytes rather than the whole grid.</summary>
    public static byte[] EncodeCells(IReadOnlyList<int> counts)
    {
        var nonZero = 0;
        for (var i = 0; i < counts.Count; i++) if (counts[i] > 0) nonZero++;
        var bytes = new byte[nonZero * 6];
        var at = 0;
        for (var i = 0; i < counts.Count && i <= ushort.MaxValue; i++)
        {
            if (counts[i] <= 0) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)i);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 2), (uint)counts[i]);
            at += 6;
        }
        return bytes;
    }

    /// <summary>Adds an <see cref="EncodeCells"/> grid into <paramref name="into"/> (cells past its
    /// end are ignored).</summary>
    public static void AddCells(byte[]? encoded, long[] into)
    {
        if (encoded is null) return;
        for (var at = 0; at + 6 <= encoded.Length; at += 6)
        {
            var index = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(at));
            if (index < into.Length) into[index] += BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(at + 2));
        }
    }

    /// <summary>A grid of a different size added into <paramref name="into"/> (<paramref name="cols"/>
    /// x <paramref name="rows"/>), each source cell landing on the target cell under its centre — so
    /// hours recorded at different grid sizes (a changed aspect or motion grid) still add up.</summary>
    public static void AddCellsResampled(byte[]? encoded, int fromCols, int fromRows, long[] into, int cols, int rows)
    {
        if (encoded is null || fromCols <= 0 || fromRows <= 0) return;
        if (fromCols == cols && fromRows == rows) { AddCells(encoded, into); return; }
        for (var at = 0; at + 6 <= encoded.Length; at += 6)
        {
            int index = BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(at));
            if (index >= fromCols * fromRows) continue;
            var x = (index % fromCols + 0.5) / fromCols;
            var y = (index / fromCols + 0.5) / fromRows;
            var target = Math.Min(rows - 1, (int)(y * rows)) * cols + Math.Min(cols - 1, (int)(x * cols));
            into[target] += BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(at + 2));
        }
    }
}
