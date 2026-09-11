using LarisVMS.Core.Enums;

namespace LarisVMS.Core;

/// <summary>
/// M18: setting keys for which camera-native object classes the Snapshots browser (Pages/Snapshots)
/// includes — added after the browser shipped showing every MotionSpan regardless of source, per
/// explicit request for an admin-configurable "which event types cameras support" filter. Same
/// per-key-string-value shape as EventColors, stored through the same global-only ISettingsResolver
/// path (Admin/Settings/Events, same page that already governs each type's timeline color) rather
/// than a dedicated table, since this is a handful of booleans with no per-camera/per-node need.
///
/// The Snapshots browser is now object-detection-only (a camera-native DetectionKind span, an
/// AI-Vision DetectedObjectCategory span, or a CustomTag rule span). Plain motion / "motion
/// detected" is unconditionally excluded there and is NOT configurable — the retired
/// "Snapshots.Enabled.Motion" key was removed. A custom EventTagRule span is likewise never gated
/// here (each rule already carries its own IsEnabled toggle), and an AI-Vision category has no
/// per-type setting. Only each closed-enum DetectionKind is gated by a key below.
///
/// Missing key = enabled (opt-out, not opt-in): the browser showed everything before this setting
/// existed, and an untouched deployment should keep showing every detected class rather than going
/// silently empty the moment this setting's resolution code shipped.
/// </summary>
public static class SnapshotVisibility
{
    /// <summary>Keyed by the enum's name, not its number — same reasoning as EventColors.DetectionKey:
    /// stays readable in the Settings table and immune to a future renumbering.</summary>
    public static string DetectionKey(DetectionKind kind) => "Snapshots.Enabled." + kind;
}
