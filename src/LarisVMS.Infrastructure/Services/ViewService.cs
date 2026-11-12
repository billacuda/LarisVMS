using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class ViewService(ApplicationDbContext db) : IViewService
{
    public async Task<List<View>> ListVisibleToAsync(string userId, CancellationToken ct = default)
        => await db.Views
            .Where(v => v.OwnerId == userId || v.IsShared)
            .OrderBy(v => v.Name)
            .ToListAsync(ct);

    public async Task<View?> GetVisibleToAsync(Guid id, string userId, CancellationToken ct = default)
        => await db.Views
            .Where(v => v.Id == id && (v.OwnerId == userId || v.IsShared))
            .FirstOrDefaultAsync(ct);

    public async Task<View> CreateAsync(string name, string ownerId, CancellationToken ct = default)
    {
        var view = new View { Id = Guid.NewGuid(), Name = name, OwnerId = ownerId, CreatedAt = DateTime.UtcNow };
        db.Views.Add(view);
        await db.SaveChangesAsync(ct);
        return view;
    }

    public async Task UpdateAsync(Guid id, string userId, string name, bool isShared, string layoutJson,
        int sequenceIntervalSeconds, bool canManageAll = false, CancellationToken ct = default)
    {
        var view = await db.Views.FirstOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new InvalidOperationException(Loc.T("server.view.viewNotFound"));
        // Someone else's view: only an administrator may change a shared one (anyone can use it).
        if (view.OwnerId != userId && !(view.IsShared && canManageAll))
            throw new UnauthorizedAccessException(Loc.T("server.view.onlyTheOwnerCanEdit"));

        view.Name = name;
        view.IsShared = isShared;
        view.LayoutJson = layoutJson;
        view.SequenceIntervalSeconds = sequenceIntervalSeconds;
        view.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, string userId, bool canManageAll, CancellationToken ct = default)
    {
        var view = await db.Views.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (view is null) return;
        if (view.OwnerId != userId && !(view.IsShared && canManageAll))
            throw new UnauthorizedAccessException(Loc.T("server.view.onlyTheOwnerCanDelete"));
        db.Views.Remove(view);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<View>> GetTourViewsAsync(CancellationToken ct = default)
        => await db.Views
            .Where(v => v.IsShared && v.SequenceIntervalSeconds > 0)
            .OrderBy(v => v.Name)
            .ToListAsync(ct);
}
