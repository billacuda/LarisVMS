namespace LarisVMS.Core.Entities;

/// <summary>
/// M18: a user-created marker at one instant on one camera's timeline, with a note — "mark this
/// moment for later" the way a physical DVR's bookmark button did, distinct from an Export (which
/// copies footage out of the system) or a snapshot tag (server-detected, not user-authored). Deleted
/// automatically once the footage it points at ages out of retention — see BookmarkRetentionService
/// — rather than left dangling as a note about a moment nothing can play back anymore.
/// </summary>
public class Bookmark
{
    public Guid Id { get; set; }
    public Guid CameraId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string Note { get; set; } = string.Empty;

    /// <summary>ApplicationUser.Id of whoever created this — auditing/attribution only, same
    /// convention as ExportJob.RequestedByUserId. Never used to filter who can see a bookmark:
    /// bookmarks are shared across every user holding Playback.View, same as an ExportJob is shared
    /// across everyone holding Exports.View.</summary>
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>Denormalized alongside CreatedByUserId, same reasoning as ExportJob's own
    /// RequestedByUserName — a snapshot of the display name at creation time, not a live join.</summary>
    public string? CreatedByUserName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
