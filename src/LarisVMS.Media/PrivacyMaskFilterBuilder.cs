using System.Globalization;

namespace LarisVMS.Media;

/// <summary>
/// Turns a camera's Privacy zones (and an export's redaction areas) into ffmpeg `drawbox` filters that
/// black out the drawn shapes. Uses ffmpeg's own `iw`/`ih` (input width/height) runtime variables
/// rather than pixel coordinates, so this needs no advance knowledge of the stream's actual resolution
/// — RecordingSession discovers that from ffmpeg's own stderr, after the command line has started.
/// </summary>
public static class PrivacyMaskFilterBuilder
{
    /// <summary>Mask grid: about 12×12 px cells at 1080p. Fine enough to follow a drawn shape, coarse
    /// enough to stay a modest number of drawbox filters.</summary>
    public const int GridWidth = 160;
    public const int GridHeight = 90;

    /// <summary>The polygons' exact shapes, as black rectangles. Each polygon is rasterized onto the
    /// mask grid (every cell its outline passes through, plus every cell whose centre is inside), the
    /// result grown by one cell all round — a privacy mask must err towards hiding too much, never
    /// leak an edge — and the covered cells merged into as few rectangles as possible: runs along
    /// each row, stacked with identical runs in the rows below. A degenerate polygon (fewer than 3
    /// points) contributes nothing.</summary>
    public static IReadOnlyList<string> BuildMaskFilters(IEnumerable<IReadOnlyList<ZoneRasterizer.Point>> polygons)
    {
        var mask = BuildMaskGrid(polygons);
        if (mask is null) return [];

        return ToRectangles(mask).Select(r => string.Create(CultureInfo.InvariantCulture,
            $"drawbox=x=iw*{r.X / (double)GridWidth:0.#####}:y=ih*{r.Y / (double)GridHeight:0.#####}:" +
            $"w=iw*{r.W / (double)GridWidth:0.#####}:h=ih*{r.H / (double)GridHeight:0.#####}:color=black:t=fill")).ToList();
    }

    /// <summary>The mask exactly as it's burnt in (grown by a cell), as a GridWidth×GridHeight grid —
    /// null when there's nothing to mask. The node uses it to drop detections inside a privacy area.</summary>
    public static bool[]? BuildMaskGrid(IEnumerable<IReadOnlyList<ZoneRasterizer.Point>> polygons)
    {
        var mask = new bool[GridWidth * GridHeight];
        var any = false;
        foreach (var polygon in polygons)
        {
            if (polygon.Count < 3) continue;
            any = true;
            mask = ZoneRasterizer.Union(mask, ZoneRasterizer.Rasterize(polygon, GridWidth, GridHeight));
            MarkOutline(mask, polygon);
        }
        return any ? Dilate(mask) : null;
    }

    /// <summary>Whether a point (0..1 frame coordinates) falls inside the burnt-in mask.</summary>
    public static bool IsMasked(bool[] grid, double x, double y)
    {
        var gx = Math.Clamp((int)(x * GridWidth), 0, GridWidth - 1);
        var gy = Math.Clamp((int)(y * GridHeight), 0, GridHeight - 1);
        return grid[gy * GridWidth + gx];
    }

    /// <summary>Kept for callers that want the old behaviour: one box per polygon's bounding box.</summary>
    public static IReadOnlyList<string> BuildDrawboxFilters(IEnumerable<IReadOnlyList<ZoneRasterizer.Point>> polygons)
    {
        var filters = new List<string>();
        foreach (var polygon in polygons)
        {
            if (polygon.Count < 3) continue;

            var xMin = Math.Clamp(polygon.Min(p => p.X), 0.0, 1.0);
            var xMax = Math.Clamp(polygon.Max(p => p.X), 0.0, 1.0);
            var yMin = Math.Clamp(polygon.Min(p => p.Y), 0.0, 1.0);
            var yMax = Math.Clamp(polygon.Max(p => p.Y), 0.0, 1.0);
            var width = xMax - xMin;
            var height = yMax - yMin;
            if (width <= 0 || height <= 0) continue;

            filters.Add(string.Create(CultureInfo.InvariantCulture,
                $"drawbox=x=iw*{xMin:0.####}:y=ih*{yMin:0.####}:w=iw*{width:0.####}:h=ih*{height:0.####}:color=black:t=fill"));
        }
        return filters;
    }

    /// <summary>Every cell the polygon's edges pass through — centre sampling alone misses thin
    /// slivers and shapes smaller than a cell.</summary>
    private static void MarkOutline(bool[] mask, IReadOnlyList<ZoneRasterizer.Point> polygon)
    {
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[j];
            var b = polygon[i];
            var steps = (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X) * GridWidth, Math.Abs(b.Y - a.Y) * GridHeight) * 2) + 1;
            for (var s = 0; s <= steps; s++)
            {
                var t = s / (double)steps;
                var x = Math.Clamp((int)((a.X + (b.X - a.X) * t) * GridWidth), 0, GridWidth - 1);
                var y = Math.Clamp((int)((a.Y + (b.Y - a.Y) * t) * GridHeight), 0, GridHeight - 1);
                mask[y * GridWidth + x] = true;
            }
        }
    }

    private static bool[] Dilate(bool[] mask)
    {
        var result = new bool[mask.Length];
        for (var y = 0; y < GridHeight; y++)
        for (var x = 0; x < GridWidth; x++)
        {
            if (!mask[y * GridWidth + x]) continue;
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var ny = y + dy;
                if (nx is >= 0 and < GridWidth && ny is >= 0 and < GridHeight) result[ny * GridWidth + nx] = true;
            }
        }
        return result;
    }

    internal readonly record struct Rect(int X, int Y, int W, int H);

    /// <summary>Covered cells as rectangles: horizontal runs per row, each extended down while the rows
    /// below have exactly the same run.</summary>
    internal static List<Rect> ToRectangles(bool[] mask)
    {
        var runsByRow = new List<(int Start, int End)>[GridHeight];
        for (var y = 0; y < GridHeight; y++)
        {
            var runs = new List<(int, int)>();
            var x = 0;
            while (x < GridWidth)
            {
                if (!mask[y * GridWidth + x]) { x++; continue; }
                var start = x;
                while (x < GridWidth && mask[y * GridWidth + x]) x++;
                runs.Add((start, x));
            }
            runsByRow[y] = runs;
        }

        var rects = new List<Rect>();
        var open = new Dictionary<(int Start, int End), int>(); // run -> first row it appeared in
        for (var y = 0; y <= GridHeight; y++)
        {
            var current = y < GridHeight ? runsByRow[y].ToHashSet() : [];
            foreach (var (run, top) in open.ToList())
            {
                if (current.Contains(run)) continue;
                rects.Add(new Rect(run.Start, top, run.End - run.Start, y - top));
                open.Remove(run);
            }
            foreach (var run in current)
                open.TryAdd(run, y);
        }
        return rects;
    }
}
