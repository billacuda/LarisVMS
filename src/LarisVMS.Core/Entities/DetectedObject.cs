namespace LarisVMS.Core.Entities;

/// <summary>
/// One tracked object inside an AI-detection <see cref="MotionSpan"/>. A span is one row per
/// *label* (two cars passing together are one "car" span), so this is where each individual object
/// is kept: its own best box, its colors and, when it drove through a speed trap, its speed.
///
/// Upserted by NodeService.RecordMotionSpansAsync from the objects Vision reports alongside each
/// span checkpoint/close, keyed on (MotionSpanId, TrackId). TrackId is ByteTrack's id and is only
/// unique within one pipeline run, which is all the span needs. Deleted with its span (cascade), so
/// span retention covers it. No foreign key to Camera or SpeedTrap: both already cascade into
/// MotionSpan, and SQL Server refuses a second cascade path to the same table.
/// </summary>
public class DetectedObject
{
    public long Id { get; set; }
    public long MotionSpanId { get; set; }

    /// <summary>The span's camera, copied here so speed queries (log, heatmap, playback markers)
    /// can seek an index without joining MotionSpans. Plain column, no foreign key — see the class
    /// comment.</summary>
    public Guid CameraId { get; set; }

    public int TrackId { get; set; }

    /// <summary>The object's own label (e.g. "car"). Normally the span's label; kept per object
    /// because the tracker's label for a track can settle after the span was opened.</summary>
    public string Label { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }

    /// <summary>The object's best frame (size × confidence), normalized 0-1 like
    /// MotionSpan.BestBox*.</summary>
    public DateTime? BestFrameAtUtc { get; set; }
    public double? BoxX { get; set; }
    public double? BoxY { get; set; }
    public double? BoxW { get; set; }
    public double? BoxH { get; set; }
    public double? Confidence { get; set; }

    /// <summary>Named colors (<see cref="ObjectColors"/> names, e.g. "red"). Null when unknown —
    /// the camera was in IR/monochrome mode, the crop was too small, or color classification is
    /// off. Upper/Lower are set for people only (shirt and pants).</summary>
    public string? PrimaryColor { get; set; }
    public string? UpperColor { get; set; }
    public string? LowerColor { get; set; }

    /// <summary>km/h, from a speed trap crossing. Null when the object never crossed both gates of
    /// one trap.</summary>
    public double? SpeedKph { get; set; }

    /// <summary>Which speed trap measured <see cref="SpeedKph"/>. Plain column, no foreign key (see
    /// the class comment); a deleted trap just leaves a dangling id.</summary>
    public Guid? SpeedTrapId { get; set; }

    /// <summary>When the speed was measured — the moment the object crossed the trap's second gate.
    /// Set with <see cref="SpeedKph"/>; rows from before this column use the best frame's time.</summary>
    public DateTime? SpeedMeasuredAtUtc { get; set; }

    public MotionSpan MotionSpan { get; set; } = null!;
}
