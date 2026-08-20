using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>
/// M18: user-created markers at a specific instant on a camera's timeline, with a note — shared
/// across every user holding Playback.View, same visibility model as an ExportJob. Entries expire on
/// their own once the footage they point at ages out of retention (BookmarkRetentionService), so
/// there is deliberately no update — a bookmark past its footage's own lifetime shouldn't be
/// editable back into relevance, only created fresh or deleted outright.
/// </summary>
public interface IBookmarkService
{
    Task<Bookmark> CreateAsync(Guid cameraId, DateTime timestampUtc, string note,
        string userId, string? userName, CancellationToken ct = default);

    /// <summary>Newest first — matches ExportJob's own listing order, and is what makes "what did I
    /// just bookmark" the first thing visible without paging.</summary>
    Task<List<BookmarkDto>> ListAsync(CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>M18: the per-camera timeline's own marker overlay — every bookmark on one camera
    /// whose instant falls in [fromUtc, toUtc), oldest first (matches the timeline's own left-to-right
    /// reading direction, the opposite of ListAsync's newest-first list-page order).</summary>
    Task<List<BookmarkMarkerDto>> ListForCameraAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
