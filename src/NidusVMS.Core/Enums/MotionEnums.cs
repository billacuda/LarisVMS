namespace NidusVMS.Core.Enums;

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
