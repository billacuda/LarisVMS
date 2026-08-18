namespace LarisVMS.Core;

/// <summary>
/// Which corner of a live tile the motion and object-detection badges sit in
/// (<c>Events.BadgeCorner</c>, global). Stored as one of these four names rather than raw CSS: the
/// value ends up driving positioning classes in the browser, and an allowlist is what keeps a stored
/// setting from becoming an injection point — the same reasoning Branding already applies to values
/// it interpolates into CSS.
///
/// Top-left is the default and the only corner with nothing else in it. The other three share space
/// with existing controls, which the client accounts for when positioning: bottom-left holds a
/// playback-mode cell's mini timeline, and bottom-right holds every tile's hover controls.
/// </summary>
public static class EventBadgeCorner
{
    public const string TopLeft = "TopLeft";
    public const string TopRight = "TopRight";
    public const string BottomRight = "BottomRight";
    public const string BottomLeft = "BottomLeft";

    public const string Default = TopLeft;

    public static readonly string[] All = [TopLeft, TopRight, BottomRight, BottomLeft];

    /// <summary>The stored value if it's one of the four, otherwise the default. Never throws — a
    /// setting written by an older or newer build, or edited directly in the database, must degrade
    /// to the default rather than break the live page.</summary>
    public static string Normalize(string? value)
        => All.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>Human-readable label for the admin dropdown.</summary>
    public static string Label(string corner) => corner switch
    {
        TopRight => "Top right",
        BottomRight => "Bottom right (shares the corner with the tile controls)",
        BottomLeft => "Bottom left (shares the corner with the playback mini timeline)",
        _ => "Top left (default)"
    };
}
