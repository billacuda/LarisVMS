using System.Text.Json;

namespace LarisVMS.Media;

/// <summary>
/// Converts a Zone's stored polygon (fractions of frame width/height, see Zone.PolygonJson) into a
/// per-pixel boolean mask at the motion pipeline's fixed 320x240 working resolution. Pure and
/// resolution-independent of the camera's actual stream — masks are built once per reconcile (or
/// zone change), not per frame, so the ray-casting cost here is irrelevant next to the per-frame
/// diff cost in MotionDetector.
/// </summary>
public static class ZoneRasterizer
{
    public readonly record struct Point(double X, double Y);

    /// <summary>Parses <c>{"points":[[x,y],[x,y],...]}</c>. Returns an empty list (not a throw) for
    /// malformed JSON — a zone editor bug or hand-edited row shouldn't take down the motion pipeline
    /// for every other zone on the camera; an empty polygon rasterizes to an all-false mask, which
    /// MotionDetector.Score already treats as "never motion" rather than a divide-by-zero.</summary>
    public static IReadOnlyList<Point> ParsePolygon(string polygonJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(polygonJson);
            if (!doc.RootElement.TryGetProperty("points", out var points) || points.ValueKind != JsonValueKind.Array)
                return [];

            var result = new List<Point>();
            foreach (var p in points.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 2) continue;
                result.Add(new Point(p[0].GetDouble(), p[1].GetDouble()));
            }
            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Rasterizes one polygon (fractional 0.0-1.0 coordinates) to a width*height boolean
    /// mask, sampling each pixel's center — needs at least 3 vertices to enclose any area.</summary>
    public static bool[] Rasterize(IReadOnlyList<Point> polygon, int width, int height)
    {
        var mask = new bool[width * height];
        if (polygon.Count < 3) return mask;

        for (var y = 0; y < height; y++)
        {
            var py = (y + 0.5) / height;
            var rowOffset = y * width;
            for (var x = 0; x < width; x++)
            {
                var px = (x + 0.5) / width;
                if (PointInPolygon(px, py, polygon)) mask[rowOffset + x] = true;
            }
        }
        return mask;
    }

    // Standard even-odd ray-casting test, points as fractional (0.0-1.0) coordinates.
    private static bool PointInPolygon(double px, double py, IReadOnlyList<Point> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if ((pi.Y > py) != (pj.Y > py) &&
                px < (pj.X - pi.X) * (py - pi.Y) / (pj.Y - pi.Y) + pi.X)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>mask[i] &amp;&amp; !exclude[i] for every pixel — a ServerMotion zone's effective mask
    /// after subtracting whatever falls inside any Ignore zone on the same camera. Lengths must
    /// match (both built at the same fixed working resolution).</summary>
    public static bool[] Subtract(bool[] mask, bool[] exclude)
    {
        var result = new bool[mask.Length];
        for (var i = 0; i < mask.Length; i++) result[i] = mask[i] && !exclude[i];
        return result;
    }

    /// <summary>Pixel-wise OR — used to combine multiple Ignore zones on the same camera into one
    /// exclusion mask before subtracting it from each ServerMotion zone.</summary>
    public static bool[] Union(bool[] a, bool[] b)
    {
        var result = new bool[a.Length];
        for (var i = 0; i < a.Length; i++) result[i] = a[i] || b[i];
        return result;
    }
}
