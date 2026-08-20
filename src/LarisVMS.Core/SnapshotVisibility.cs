using LarisVMS.Core.Enums;

namespace LarisVMS.Core;

/// <summary>
/// M18: setting keys for which motion-event types the Snapshots browser (Pages/Snapshots) includes —
/// added after the browser shipped showing every MotionSpan regardless of source, per explicit
/// request for an admin-configurable "which event types cameras support" filter. Same
/// per-key-string-value shape as EventColors, stored through the same global-only ISettingsResolver
/// path (Admin/Settings/Events, same page that already governs each type's timeline color) rather
/// than a dedicated table, since this is a handful of booleans with no per-camera/per-node need.
///
/// A custom EventTagRule-sourced span is never filtered by this — each rule already carries its own
/// IsEnabled toggle, a deliberate per-rule admin choice, so filtering it again at the type level here
/// would be redundant. Only plain motion (no class, no rule) and each DetectionKind are gated.
///
/// Missing key = enabled (opt-out, not opt-in): the browser showed everything before this setting
/// existed, and an untouched deployment should keep doing so rather than going silently empty the
/// moment this setting's resolution code shipped.
/// </summary>
public static class SnapshotVisibility
{
    public const string MotionKey = "Snapshots.Enabled.Motion";

    /// <summary>Keyed by the enum's name, not its number — same reasoning as EventColors.DetectionKey:
    /// stays readable in the Settings table and immune to a future renumbering.</summary>
    public static string DetectionKey(DetectionKind kind) => "Snapshots.Enabled." + kind;
}
