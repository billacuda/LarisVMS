namespace NidusVMS.Core.Entities;

/// <summary>
/// A user-configured rule turning ONVIF PullPoint notifications into a named, colored timeline tag —
/// the generalization of M8 pass 6's hardcoded motion classifier to arbitrary event types (object
/// detection, tamper, digital input, anything a camera's own analytics stack reports). "Probed" in
/// the sense that the topics a user picks from come from this camera's own observed CameraEvents
/// history, not a hand-typed guess — see EventTagsModel.
///
/// StartTopic/StopTopic mirror the user's own framing ("a start event and a matching stop event"):
/// most ONVIF boolean-state topics (motion included) use one topic for both edges, distinguished by
/// a State/IsMotion item — that's StopTopic left null, and the rule reads the state item off
/// StartTopic itself. Some vendors' analytics genuinely use two distinct topics for the two edges
/// (e.g. an "ObjectAppeared"/"ObjectDisappeared" pair) — that's StopTopic set, and the rule treats
/// any StartTopic notification as the rising edge and any StopTopic one as the falling edge,
/// regardless of that notification's own state item.
/// </summary>
public class EventTagRule
{
    public Guid Id { get; set; }
    public Guid CameraId { get; set; }

    /// <summary>User-facing label, e.g. "Vehicle", "Person", "Tamper" — shown on the timeline and in
    /// the rule list, distinct from the raw ONVIF topic string underneath it.</summary>
    public string Name { get; set; } = string.Empty;

    public string StartTopic { get; set; } = string.Empty;

    /// <summary>Null means StartTopic is a toggle topic — the rule reads its own State/IsMotion
    /// SimpleItem to know which edge a given notification is. Non-null means StartTopic and
    /// StopTopic are two genuinely different topics, one per edge.</summary>
    public string? StopTopic { get; set; }

    /// <summary>Hex color (e.g. "#ffc107") the timeline renders this tag's spans in — takes
    /// precedence over the built-in green/blue motion-or-recording coloring wherever this rule is
    /// active, the same way motion already takes precedence over plain recording.</summary>
    public string ColorHex { get; set; } = "#ffc107";

    /// <summary>Whether this tag also acts as a Motion-mode recording-gating signal, the same way
    /// the built-in motion classifier already does — a segment is kept if any *enabled,
    /// DrivesRecording* rule (or the built-in motion detector) saw activity in its window, on top of
    /// whatever ServerMotion zones/CameraEvent motion already contribute. False means this tag is
    /// purely informational — shows on the timeline, never affects what gets kept.</summary>
    public bool DrivesRecording { get; set; }

    public bool IsEnabled { get; set; } = true;

    public Camera Camera { get; set; } = null!;
}
