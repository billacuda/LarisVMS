namespace LarisVMS.Core.Enums;

public enum ZoneKind
{
    /// <summary>Frame-diff motion detection runs server-side (node substream pipeline) restricted
    /// to this polygon. The only kind that produces MotionSpans in M8 pass 1 — CameraMotion is
    /// stored but not yet pushed to the device (that needs per-vendor SetVideoAnalyticsConfiguration
    /// work, deferred to a follow-up pass).</summary>
    ServerMotion = 0,

    /// <summary>Camera-side motion zone, pushed to the device over ONVIF. Deferred: stored and
    /// editable now so the one zone editor covers both kinds from day one (matches the plan), but
    /// nothing pushes it to a device yet — PushedToCameraAt stays null until that lands.</summary>
    CameraMotion = 1,

    /// <summary>Marked for future server-side burn-in on the transcode path (M9). Stored only in
    /// M8 pass 1 — not yet excluded from motion scoring or rendered anywhere.</summary>
    Privacy = 2,

    /// <summary>Pixels inside this polygon are excluded from the ServerMotion frame-diff sum —
    /// the "zone mask" the plan describes (a tree that sways in the wind, a public sidewalk at the
    /// edge of frame). Overlaps a ServerMotion zone by area, not by owning it — see
    /// MotionDetector.Score.</summary>
    Ignore = 3
}

/// <summary>How a MotionSpan was produced. ServerMotion is a drawn zone's own frame-diff detection;
/// CameraEvent is the built-in ONVIF motion classifier (CameraEventClassifier's hardcoded topic
/// markers); CustomTag (M8 pass 8) is a user-configured EventTagRule — see MotionSpan.EventTagRuleId,
/// set only for this source.</summary>
public enum MotionSource
{
    ServerMotion = 0,
    CameraEvent = 1,
    CustomTag = 2
}

/// <summary>What a camera's own onboard analytics said it saw, when its notification identifies an
/// object class rather than just "something moved" — see CameraEventClassifier.ClassifyDetection.
///
/// Deliberately a separate nullable column on MotionSpan rather than another MotionSource value:
/// Source answers "which subsystem produced this span" and an object detection is still a
/// CameraEvent (same PullPoint channel, same classifier), while this answers the independent
/// question "what was detected". Folding them together would make the two unaskable separately, and
/// would mean every existing consumer of Source had to learn about object classes to keep working.
///
/// Not a bounding box or a count — ONVIF's rule-engine topics report that an object class was seen,
/// not where it was. Per-frame coordinates ride the metadata RTP track, which this app doesn't
/// consume (see the CHANGELOG's note on bounding boxes).</summary>
/// Values are persisted as integers on MotionSpan.DetectionKind, so existing numbers must never be
/// reused or renumbered — new classes are only ever appended.
public enum DetectionKind
{
    Person = 0,
    Vehicle = 1,
    Face = 2,

    /// <summary>A recognized object-detection topic whose class this app doesn't model specifically
    /// — still worth surfacing as "the camera detected something it classifies as an object" rather
    /// than discarding, since that's already more than plain motion tells you.</summary>
    Other = 3,

    Animal = 4,

    /// <summary>An object left in the scene that wasn't there before — the abandoned-baggage case,
    /// which is what vendors mean by "left"/"abandoned object" detection.</summary>
    ObjectAppeared = 5,

    /// <summary>An object that was in the scene and is now gone — vendors call this
    /// "taken away"/"missing object" detection.</summary>
    ObjectMissing = 6
}

/// <summary>How each detected class is presented — one source of truth so the timeline's colors and
/// the live tile's badge can't drift apart. Colors are deliberately well clear of the timeline's
/// existing motion green and recorded blue, so "a person was here" never reads as ordinary motion.</summary>
public static class DetectionDisplay
{
    /// <summary>Every class, in the order they should be listed in a UI. Iterating this rather than
    /// Enum.GetValues keeps the admin colour editor's ordering deliberate instead of tied to the
    /// numeric values, which exist only for storage.</summary>
    public static IReadOnlyList<DetectionKind> AllKinds { get; } =
    [
        DetectionKind.Person,
        DetectionKind.Vehicle,
        DetectionKind.Face,
        DetectionKind.Animal,
        DetectionKind.ObjectAppeared,
        DetectionKind.ObjectMissing,
        DetectionKind.Other
    ];

    /// <summary>The built-in colour for a class, used when an admin hasn't chosen one. Deliberately
    /// well clear of the timeline's motion and recording colours, so "a person was here" never reads
    /// as ordinary motion.</summary>
    public static string ColorHex(DetectionKind kind) => kind switch
    {
        DetectionKind.Person => "#ff9f43",
        DetectionKind.Vehicle => "#a78bfa",
        DetectionKind.Face => "#f472b6",
        DetectionKind.Animal => "#fbbf24",
        DetectionKind.ObjectAppeared => "#14b8a6",
        DetectionKind.ObjectMissing => "#f87171",
        _ => "#22d3ee"
    };

    public static string Label(DetectionKind kind) => kind switch
    {
        DetectionKind.Person => "Person",
        DetectionKind.Vehicle => "Vehicle",
        DetectionKind.Face => "Face",
        DetectionKind.Animal => "Animal",
        DetectionKind.ObjectAppeared => "Object appeared",
        DetectionKind.ObjectMissing => "Object missing",
        _ => "Object"
    };

    /// <summary>Emoji marker for the live-tile badge, matching this app's emoji-as-icons convention.
    /// Other is a package rather than a magnifying glass: the badge names what the camera saw, and a
    /// magnifier reads as an action (search) rather than a thing.</summary>
    public static string Emoji(DetectionKind kind) => kind switch
    {
        DetectionKind.Person => "🚶",
        DetectionKind.Vehicle => "🚗",
        DetectionKind.Face => "🙂",
        DetectionKind.Animal => "🐾",
        DetectionKind.ObjectAppeared => "🧳",
        DetectionKind.ObjectMissing => "❓",
        _ => "📦"
    };
}
