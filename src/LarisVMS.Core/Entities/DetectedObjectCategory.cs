namespace LarisVMS.Core.Entities;

/// <summary>
/// An auto-registering, auto-colored bucket AI-detected object labels are grouped into for
/// display purposes — "Human", "Vehicle", "Animal", "Object" (the catch-all for everything else
/// COCO reports), per the object detection plan's decision 5. Deliberately not the closed 6-value
/// DetectionKind enum: DetectionKind covers camera-native (ONVIF/vendor CGI) detections and stays
/// completely untouched by this table.
///
/// Small and mostly-fixed in practice — CocoCategoryMap resolves a raw class name (e.g. "car",
/// "dog") to one of a handful of category names, so this table only ever grows a new row the first
/// time a genuinely new category name is ever reported, not once per COCO class. Rows are created
/// exclusively by NodeService.RecordMotionSpansAsync (the only side with DB access) the first time
/// a name is seen; the reporting node never assigns the Id or ColorHex itself.
/// </summary>
public class DetectedObjectCategory
{
    public Guid Id { get; set; }

    /// <summary>e.g. "Human", "Vehicle", "Animal", "Object" — matched case-insensitively on
    /// find-or-create, and enforced unique at the database level (case-insensitive collation) so a
    /// race between two nodes reporting the same brand-new category at once can't create duplicate
    /// rows with different colors.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex color (e.g. "#a78bfa"), auto-assigned once at creation by
    /// DetectedObjectColorAssigner.PickNextColor — never re-chosen afterward, so a category's color
    /// stays stable for as long as the row exists.</summary>
    public string ColorHex { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
}
