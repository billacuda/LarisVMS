namespace LarisVMS.Core;

/// <summary>
/// Picks an unused color for a newly-seen DetectedObjectCategory (object detection plan decision
/// 5) — "pick one automatically that hasn't been used yet." Called by
/// NodeService.RecordMotionSpansAsync exactly once, the first time a category name is ever seen
/// (find-or-create); in practice this fires at most a handful of times total across a
/// deployment's whole lifetime, since the category set itself is small and mostly fixed
/// (CocoCategoryMap resolves to one of a handful of names, not one per COCO class).
/// </summary>
public static class DetectedObjectColorAssigner
{
    /// <summary>Curated categorical hex palette — visually distinct from each other and
    /// deliberately clear of the timeline's existing motion-green (#28e070) and recording-blue
    /// (#1e6fd9) defaults, and of DetectionKind's own existing palette (DetectionDisplay.ColorHex),
    /// the same deliberateness that palette's own doc comment describes. Sized generously past any
    /// realistic number of categories a deployment would ever accumulate.</summary>
    private static readonly IReadOnlyList<string> Palette =
    [
        "#ef4444", "#f97316", "#eab308", "#d946ef", "#8b5cf6", "#06b6d4",
        "#fb7185", "#a3e635", "#c084fc", "#fdba74", "#67e8f9", "#fde047",
        "#f0abfc", "#fca5a5", "#d8b4fe", "#5eead4",
    ];

    /// <summary>Returns the first palette entry not already in
    /// <paramref name="existingColorsInUse"/> (case-insensitive), or — only once every curated
    /// entry is already taken, which the palette is sized to make practically unreachable — a
    /// deterministic hash-derived color so the fallback is at least stable and reproducible rather
    /// than random.</summary>
    public static string PickNextColor(IReadOnlyCollection<string> existingColorsInUse)
    {
        ArgumentNullException.ThrowIfNull(existingColorsInUse);

        var used = new HashSet<string>(existingColorsInUse, StringComparer.OrdinalIgnoreCase);
        foreach (var color in Palette)
        {
            if (!used.Contains(color)) return color;
        }

        return HashDerivedColor(existingColorsInUse.Count);
    }

    /// <summary>Golden-angle hue stepping keeps consecutive fallback colors visually distinct from
    /// one another even though they're generated rather than curated — the same technique
    /// categorical-palette generators commonly use for "as many distinct hues as needed."</summary>
    private static string HashDerivedColor(int seed)
    {
        var hue = seed * 137.508 % 360.0;
        var (r, g, b) = HsvToRgb(hue, saturation: 0.65, value: 0.85);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    private static (byte R, byte G, byte B) HsvToRgb(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
