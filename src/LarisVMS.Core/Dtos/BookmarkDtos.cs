namespace LarisVMS.Core.Dtos;

/// <summary>One Bookmark plus the camera name it's denormalized against for display — same
/// "resolve the name once server-side rather than making the client join" shape ExportJob's own
/// listing endpoint already uses for camera/node names.</summary>
public record BookmarkDto(Guid Id, Guid CameraId, string CameraName, DateTime TimestampUtc,
    string Note, string? CreatedByUserName, DateTime CreatedAt);

public record CreateBookmarkRequest(Guid CameraId, DateTime TimestampUtc, string Note);

/// <summary>M18: the per-camera timeline's own marker overlay — lighter than BookmarkDto (no camera
/// name; the timeline already knows which single camera it's drawing) and scoped to one camera's
/// [from, to) range instead of every bookmark in the system.</summary>
public record BookmarkMarkerDto(Guid Id, DateTime TimestampUtc, string Note);
