using System.Globalization;

namespace LarisVMS.Media;

/// <summary>
/// M18: turns a camera's Privacy-kind Zones into ffmpeg `-vf drawbox` filter expressions —
/// EncodePipeline's first real consumer. Uses ffmpeg's own `iw`/`ih` (input width/height) runtime
/// variables rather than pixel coordinates, so this needs no advance knowledge of the stream's actual
/// resolution — RecordingSession discovers that dynamically from ffmpeg's own stderr, well after the
/// process (and its command line) already started, so a filter expressed in pixels would have nothing
/// to compute from until it was too late to matter.
/// </summary>
public static class PrivacyMaskFilterBuilder
{
    /// <summary>One `drawbox` per polygon, burning in that polygon's axis-aligned bounding box —
    /// deliberately the bounding box, not the exact drawn outline. Over-masking a few extra pixels
    /// around an oddly-shaped zone is the safe default for a privacy control (better to hide too much
    /// than too little); rendering the precise polygon shape would need a per-pixel mask image and
    /// ffmpeg's `overlay` filter, real added complexity for a shape difference that only ever matters
    /// at the mask's own edge. A degenerate polygon (fewer than 3 points, or a zero-area bounding box
    /// after clamping to the valid 0..1 frame) contributes nothing rather than a 0x0 drawbox.</summary>
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
}
