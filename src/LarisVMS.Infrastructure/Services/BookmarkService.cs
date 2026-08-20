using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class BookmarkService(ApplicationDbContext db) : IBookmarkService
{
    public async Task<Bookmark> CreateAsync(Guid cameraId, DateTime timestampUtc, string note,
        string userId, string? userName, CancellationToken ct = default)
    {
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            CameraId = cameraId,
            TimestampUtc = timestampUtc,
            Note = note,
            CreatedByUserId = userId,
            CreatedByUserName = userName
        };
        db.Bookmarks.Add(bookmark);
        await db.SaveChangesAsync(ct);
        return bookmark;
    }

    public async Task<List<BookmarkDto>> ListAsync(CancellationToken ct = default) =>
        await (
            from b in db.Bookmarks.AsNoTracking()
            join camera in db.Cameras.AsNoTracking() on b.CameraId equals camera.Id into cameraJoin
            from camera in cameraJoin.DefaultIfEmpty()
            orderby b.TimestampUtc descending
            select new BookmarkDto(b.Id, b.CameraId, camera != null ? camera.Name : "(deleted camera)",
                b.TimestampUtc, b.Note, b.CreatedByUserName, b.CreatedAt)
        ).ToListAsync(ct);

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await db.Bookmarks.Where(b => b.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<List<BookmarkMarkerDto>> ListForCameraAsync(Guid cameraId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
        await db.Bookmarks.AsNoTracking()
            .Where(b => b.CameraId == cameraId && b.TimestampUtc >= fromUtc && b.TimestampUtc < toUtc)
            .OrderBy(b => b.TimestampUtc)
            .Select(b => new BookmarkMarkerDto(b.Id, b.TimestampUtc, b.Note))
            .ToListAsync(ct);
}
