namespace LarisVMS.Core;

/// <summary>
/// The fixed set of color names object color classification reports, with the swatch each badge is
/// painted in. One list shared by Vision (which picks the name), the Snapshots filter tree and the
/// badges, so a name can never be produced that the UI doesn't know how to show.
/// </summary>
public static class ObjectColors
{
    public sealed record ObjectColor(string Name, string SwatchHex, string TextHex);

    /// <summary>In the order the filter tree lists them.</summary>
    public static readonly IReadOnlyList<ObjectColor> All =
    [
        new("black", "#1f1f1f", "#ffffff"),
        new("gray", "#7a7a7a", "#ffffff"),
        new("silver", "#c0c4c8", "#1f1f1f"),
        new("white", "#f4f4f4", "#1f1f1f"),
        new("red", "#c62828", "#ffffff"),
        new("orange", "#ef6c00", "#ffffff"),
        new("yellow", "#f9d71c", "#1f1f1f"),
        new("green", "#2e7d32", "#ffffff"),
        new("blue", "#1565c0", "#ffffff"),
        new("purple", "#6a1b9a", "#ffffff"),
        new("pink", "#e91e8c", "#ffffff"),
        new("brown", "#6d4c41", "#ffffff"),
    ];

    private static readonly Dictionary<string, ObjectColor> ByName =
        All.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    public static ObjectColor? Find(string? name) =>
        name is not null && ByName.TryGetValue(name, out var c) ? c : null;

    public static bool IsKnown(string? name) => Find(name) is not null;

    public static string Display(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];
}
