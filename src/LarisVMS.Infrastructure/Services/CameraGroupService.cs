using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class CameraGroupService(ApplicationDbContext db) : ICameraGroupService
{
    public async Task<List<CameraGroup>> GetTreeAsync(CancellationToken ct = default)
        => await db.CameraGroups.AsNoTracking().OrderBy(g => g.MaterializedPath).ToListAsync(ct);

    public async Task<CameraGroup> CreateAsync(string name, Guid? parentId, CancellationToken ct = default)
    {
        string parentPath = "/";
        if (parentId is not null)
        {
            var parent = await db.CameraGroups.FindAsync([parentId.Value], ct)
                ?? throw new InvalidOperationException("Parent group not found.");
            parentPath = parent.MaterializedPath;
        }

        var group = new CameraGroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            ParentId = parentId
        };
        // MaterializedPath is built from the *new* group's own id so it's stable even if the group
        // is later renamed — renaming never has to cascade-rewrite every descendant's path.
        group.MaterializedPath = $"{parentPath}{group.Id}/";

        db.CameraGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var group = await db.CameraGroups.FindAsync([id], ct);
        if (group is null) return;

        var hasChildren = await db.CameraGroups.AnyAsync(g => g.ParentId == id, ct);
        if (hasChildren)
            throw new InvalidOperationException("Cannot delete a group that has child groups.");

        db.CameraGroups.Remove(group);
        await db.SaveChangesAsync(ct);
    }
}
