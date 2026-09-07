using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// One continuous span of detected motion for one camera. Volume table, same clustering reasoning
/// as <see cref="Segment"/> — every real query is "this camera, this time range" (timeline
/// bucketing, the merged all-cameras timeline), so it's clustered on (CameraId, StartUtc) from the
/// start, ahead of the same M4 date-partitioning work Segment is already positioned for.
/// </summary>
public class MotionSpan
{
    public long Id { get; set; }
    public Guid CameraId { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    public MotionSource Source { get; set; }

    /// <summary>Which zone triggered this span — null for a CameraEvent-sourced span with no
    /// zone concept of its own (a follow-up pass concern; unused by ServerMotion spans as of pass 1,
    /// which always populate it). SetNull on delete: removing a zone shouldn't delete the motion
    /// history it recorded, only stop attributing future spans to it.</summary>
    public Guid? ZoneId { get; set; }

    /// <summary>Which EventTagRule produced this span — set only when Source is CustomTag (M8 pass 8),
    /// null for ServerMotion/CameraEvent. Restrict on delete, same reasoning as ZoneId: deleting a
    /// rule shouldn't delete the tag history it recorded — EventTagRuleService nulls this out
    /// explicitly first, the same pattern ZoneService already uses for ZoneId.</summary>
    public Guid? EventTagRuleId { get; set; }

    /// <summary>What the camera's own analytics classified, for a span produced by an
    /// object-detection topic (person/vehicle/face) rather than a plain motion one. Null for every
    /// other span — including every span that existed before this column did — so nothing about
    /// ServerMotion/CustomTag/plain-motion behavior changes. See DetectionKind's own doc comment for
    /// why this is a separate column rather than another MotionSource value. Always null for an
    /// AiDetection-sourced span — see DetectedObjectCategoryId below, the open-ended equivalent for
    /// AI-vision detections specifically (object detection plan decision 5).</summary>
    public DetectionKind? DetectionKind { get; set; }

    /// <summary>Which auto-registered, auto-colored category (object detection plan decision 5 —
    /// "Human"/"Vehicle"/"Animal"/"Object", not DetectionKind's closed 6-value enum) this
    /// AI-detected object falls under. Set only for Source == AiDetection; drives the timeline's
    /// color the same way DetectionKind does for a camera-native detection. SetNull on delete: a
    /// category is never expected to be deleted, but if it ever is, the span shouldn't vanish with
    /// it, matching ZoneId/EventTagRuleId's own SetNull precedent above.</summary>
    public Guid? DetectedObjectCategoryId { get; set; }

    /// <summary>The specific class AI detection reported (e.g. "car", "backpack", "dog") — plain
    /// descriptive text, deliberately with no color or dedup table of its own (see decision 5): the
    /// category drives color, this rides alongside it purely for the "category — label" detail
    /// text a snapshot or timeline entry shows. Null for every span not sourced from AI detection.</summary>
    public string? DetectedObjectLabel { get; set; }

    /// <summary>The UTC timestamp of whichever frame, across this whole detection's lifetime, had
    /// the best combination of box size and confidence (object detection plan decision 10) — what
    /// a snapshot crop is drawn from, resolved back to a segment file + offset the same way an
    /// ordinary hover-thumbnail lookup already is. Null for every non-AiDetection span, and for an
    /// AiDetection span whose Vision-side tracking never recorded one (shouldn't happen in
    /// practice, but not assumed).</summary>
    public DateTime? BestFrameAtUtc { get; set; }

    /// <summary>The best frame's own bounding box, normalized 0-1 against whatever resolution
    /// detection ran at (the Sub stream — see decision 10's Main/Sub field-of-view assumption for
    /// how this maps onto the recorded frame the crop is actually taken from). Null together with
    /// BestFrameAtUtc.</summary>
    public double? BestBoxX { get; set; }
    public double? BestBoxY { get; set; }
    public double? BestBoxW { get; set; }
    public double? BestBoxH { get; set; }

    /// <summary>The best frame's own detection confidence — not itself used to draw the crop, but
    /// cheap to keep alongside the box it was chosen for, for future debugging/re-ranking.</summary>
    public double? BestBoxConfidence { get; set; }

    /// <summary>0.0-1.0, the peak per-frame score (see MotionDetector.Score) observed during this
    /// span — lets the timeline or a future alert rule distinguish "a leaf blew past the sensitivity
    /// threshold once" from "someone walked through for ten seconds," without storing every frame.</summary>
    public double Score { get; set; }

    /// <summary>Object-detection spans only: the peak number of distinct objects of this span's
    /// label seen moving in a single frame over its lifetime (kept as a running max across
    /// checkpoint/coalesce updates, same as Score). Surfaced as the "x2" / "x3" count on the
    /// snapshot badge — because Vision debounces one span per label, this is where the multiplicity
    /// that per-label grouping collapses ("two people walked by") is preserved. Null for every
    /// non-AiDetection span and for every span that existed before this column did; 1 or absent
    /// renders with no count suffix.</summary>
    public int? MovingCount { get; set; }

    public Camera Camera { get; set; } = null!;
    public Zone? Zone { get; set; }
    public EventTagRule? EventTagRule { get; set; }
    public DetectedObjectCategory? DetectedObjectCategory { get; set; }
}
