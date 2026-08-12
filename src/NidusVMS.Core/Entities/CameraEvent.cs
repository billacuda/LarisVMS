namespace NidusVMS.Core.Entities;

/// <summary>
/// One raw ONVIF PullPoint notification a node received for a camera — every event the camera's
/// own analytics/alarm stack reports, not just motion (tamper, digital input, audio, etc.). Motion-
/// classified events (see CameraEventClassifier) additionally produce a MotionSpan so they drive the
/// same timeline coloring and recording-gating pipeline ServerMotion zones already do; this table is
/// the full raw log underneath that, plus the only place a non-motion event is visible at all in
/// M8 pass 6 — no dedicated viewer page yet, a follow-up polish pass' concern.
/// </summary>
public class CameraEvent
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }

    /// <summary>The ONVIF topic string as reported (e.g. "tns1:RuleEngine/CellMotionDetector/Motion"),
    /// vendor/firmware-specific beyond the common motion-family markers CameraEventClassifier matches.</summary>
    public string OnvifTopic { get; set; } = string.Empty;

    public DateTime ReceivedUtc { get; set; }

    /// <summary>The notification's SimpleItem name/value pairs, JSON-serialized — kept raw rather
    /// than modeled per-topic since the item set varies by vendor and by which of dozens of possible
    /// ONVIF event types this is.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Not populated in M8 pass 6 — always false. Reserved for a future pass that wires
    /// Recording.Mode="Event" to actually start/extend recording off a non-motion event the way
    /// Motion mode already does off MotionSpans; motion-classified events already drive recording
    /// today via the MotionSpan they also produce, independent of this column.</summary>
    public bool TriggeredRecording { get; set; }

    public Camera Camera { get; set; } = null!;
}
