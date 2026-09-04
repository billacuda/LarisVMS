using LarisVMS.Core.Enums;

namespace LarisVMS.Core;

/// <summary>The colours every timeline and badge in the app draws with. Null for a slot means "use
/// the built-in default", so an untouched deployment stores nothing at all and a later change to a
/// default still reaches it.</summary>
/// <param name="Motion">Plain pixel/PullPoint motion with no object class attached.</param>
/// <param name="Recording">A bucket with footage but no motion.</param>
/// <param name="Detections">Per-object-class overrides, keyed by kind. Absent key = default.</param>
public sealed record EventPalette(
    string? Motion,
    string? Recording,
    IReadOnlyDictionary<DetectionKind, string> Detections)
{
    public static EventPalette Empty { get; } =
        new(null, null, new Dictionary<DetectionKind, string>());

    /// <summary>The colour to draw for plain motion. Blank counts as unset, not as a colour —
    /// clearing a field on the admin page stores "" rather than deleting the row, so a plain null
    /// check here would hand an empty string to a canvas fill style.</summary>
    public string MotionColor => string.IsNullOrWhiteSpace(Motion) ? EventColors.DefaultMotion : Motion;

    /// <summary>The colour to draw for recorded-but-no-motion coverage. Blank counts as unset, same
    /// as MotionColor above.</summary>
    public string RecordingColor => string.IsNullOrWhiteSpace(Recording) ? EventColors.DefaultRecording : Recording;

    /// <summary>The colour to draw for an object class, falling back to its built-in default.</summary>
    public string ColorFor(DetectionKind kind) =>
        Detections.TryGetValue(kind, out var hex) && !string.IsNullOrWhiteSpace(hex)
            ? hex
            : DetectionDisplay.ColorHex(kind);

    /// <summary>
    /// The colour to draw for an AI-detection span, by its DetectedObjectCategory name.
    ///
    /// Prefers this palette over the category row's own stored ColorHex, which is the point: that
    /// stored colour is auto-assigned by DetectedObjectColorAssigner when a category first appears and
    /// is never surfaced in the Events settings editor, so an admin who set "Human" to orange there
    /// still saw AI-detected humans badged in whatever the assigner happened to pick — and a
    /// camera-classified human and an AI-detected one in the same list came out different colours.
    /// Mapping through <see cref="CocoCategoryMap.ToDetectionKind"/> makes the Events tab the single
    /// place that decides, for both pipelines at once.
    ///
    /// <paramref name="storedCategoryColorHex"/> still wins for any category name with no
    /// DetectionKind equivalent — nothing in the Events editor describes such a category, so its own
    /// assigned colour remains the only thing that could.
    /// </summary>
    public string ColorForAiCategory(string? categoryName, string? storedCategoryColorHex) =>
        categoryName is not null && CocoCategoryMap.ToDetectionKind(categoryName) is { } kind
            ? ColorFor(kind)
            : storedCategoryColorHex is { } stored && !string.IsNullOrWhiteSpace(stored)
                ? stored
                : EventColors.DefaultMotion;
}

/// <summary>Setting keys and built-in defaults for the event palette. Kept beside the palette itself
/// so the storage contract and the rendering contract can't drift apart.</summary>
public static class EventColors
{
    /// <summary>Matches timeline.js's own fallback, which still applies before the palette has been
    /// fetched (and if that fetch ever fails) — the two must stay in step.</summary>
    public const string DefaultMotion = "#28e070";
    public const string DefaultRecording = "#1e6fd9";

    public const string MotionKey = "Timeline.Color.Motion";
    public const string RecordingKey = "Timeline.Color.Recording";

    /// <summary>Keyed by the enum's *name*, not its number, so the stored settings stay readable and
    /// a renumbering could never silently repoint a colour at a different class.</summary>
    public static string DetectionKey(DetectionKind kind) => "Timeline.Color." + kind;

    /// <summary>Emoji for plain motion — a swirl rather than any object glyph, since the whole point
    /// of this marker is that the camera saw movement it could *not* classify.</summary>
    public const string MotionEmoji = "🌀";

    /// <summary>Validates a user-supplied colour. Hex only, and reused from Branding rather than
    /// reimplemented: these values are interpolated into inline styles and canvas fill styles, so the
    /// same allowlist that keeps branding colours safe applies here for the same reason.</summary>
    public static string? Normalize(string? hex) => Branding.NormalizeColor(hex);
}
