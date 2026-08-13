using NidusVMS.Core.Enums;

namespace NidusVMS.Core.Entities;

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

    /// <summary>0.0-1.0, the peak per-frame score (see MotionDetector.Score) observed during this
    /// span — lets the timeline or a future alert rule distinguish "a leaf blew past the sensitivity
    /// threshold once" from "someone walked through for ten seconds," without storing every frame.</summary>
    public double Score { get; set; }

    public Camera Camera { get; set; } = null!;
    public Zone? Zone { get; set; }
    public EventTagRule? EventTagRule { get; set; }
}
