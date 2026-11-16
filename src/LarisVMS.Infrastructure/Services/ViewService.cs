using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Localization;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

/// <inheritdoc cref="IViewService" />
public class ViewService(ApplicationDbContext db) : IViewService
{
    public const int MaxNameLength = 200;

    // ── Who may see and change what ─────────────────────────────────────────

    /// <summary>One user's standing for view trees, read from the database (not the cookie), so a
    /// role change applies on the next request.</summary>
    private sealed record Access(string UserId, HashSet<string> RoleIds, bool IsSuperAdmin, bool HasViewsEdit,
        HashSet<string> GrantedRoleTrees)
    {
        public bool CanSee(string? roleId, string ownerId) => roleId is null
            ? ownerId == UserId
            : IsSuperAdmin || RoleIds.Contains(roleId) || GrantedRoleTrees.Contains(roleId);

        public bool CanEdit(string? roleId, string ownerId) => roleId is null
            ? ownerId == UserId
            : IsSuperAdmin || (HasViewsEdit && RoleIds.Contains(roleId));
    }

    private async Task<Access> AccessAsync(string userId, CancellationToken ct)
    {
        var roleIds = (await db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync(ct)).ToHashSet();
        if (roleIds.Count == 0) return new Access(userId, roleIds, false, false, []);

        // Super Admin by tag, not by the (renamable) role name — the same rule PermissionService uses.
        var isSuper = await db.RoleProfiles.AnyAsync(p => roleIds.Contains(p.RoleId) && p.Tag == RoleTags.SuperAdmin, ct);
        var hasEdit = isSuper || await db.Permissions.AnyAsync(p => roleIds.Contains(p.RoleId) && p.Resource == "Views" && p.Action == "Edit", ct);
        var granted = (await db.ViewTreeGrants.Where(g => roleIds.Contains(g.GrantedRoleId)).Select(g => g.OwnerRoleId).ToListAsync(ct)).ToHashSet();
        return new Access(userId, roleIds, isSuper, hasEdit, granted);
    }

    /// <summary>Role trees this user can see: every role for a Super Admin, otherwise their own roles
    /// and the ones granted to them.</summary>
    private async Task<HashSet<string>> VisibleRoleIdsAsync(Access access, CancellationToken ct) => access.IsSuperAdmin
        ? (await db.Roles.Select(r => r.Id).ToListAsync(ct)).ToHashSet()
        : access.RoleIds.Union(access.GrantedRoleTrees).ToHashSet();

    private IQueryable<View> VisibleViews(string userId, HashSet<string> roleIds) =>
        db.Views.Where(v => (v.RoleId == null && v.OwnerId == userId) || (v.RoleId != null && roleIds.Contains(v.RoleId)));

    /// <summary>The tree a name refers to, checked to exist and to be editable by this user.</summary>
    private async Task<(Access Access, string? RoleId)> EditableTreeAsync(string userId, string tree, CancellationToken ct)
    {
        if (!ViewTrees.TryParse(tree, out var roleId)
            || (roleId is not null && !await db.Roles.AnyAsync(r => r.Id == roleId, ct)))
            throw new ViewTreeException(Loc.T("server.view.folderGone"));
        var access = await AccessAsync(userId, ct);
        if (!access.CanEdit(roleId, userId))
            throw new UnauthorizedAccessException(Loc.T("server.view.cantChangeThere"));
        return (access, roleId);
    }

    private IQueryable<View> ViewsIn(string? roleId, string userId) => roleId is null
        ? db.Views.Where(v => v.RoleId == null && v.OwnerId == userId)
        : db.Views.Where(v => v.RoleId == roleId);

    private IQueryable<ViewFolder> FoldersIn(string? roleId, string userId) => roleId is null
        ? db.ViewFolders.Where(f => f.RoleId == null && f.UserId == userId)
        : db.ViewFolders.Where(f => f.RoleId == roleId);

    /// <summary>The user a Private tree belongs to: for a view or folder already in one, its owner.</summary>
    private static string TreeOwner(View view) => view.OwnerId;
    private static string TreeOwner(ViewFolder folder) => folder.UserId ?? string.Empty;

    private async Task EnsureFolderInTreeAsync(Guid? folderId, string? roleId, string userId, CancellationToken ct)
    {
        if (folderId is { } id && !await FoldersIn(roleId, userId).AnyAsync(f => f.Id == id, ct))
            throw new ViewTreeException(Loc.T("server.view.folderGone"));
    }

    private static string CleanName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new ViewTreeException(Loc.T("server.view.nameRequired"));
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private async Task<HashSet<string>> ViewNamesAsync(string? roleId, string userId, Guid? except, CancellationToken ct) =>
        (await ViewsIn(roleId, userId).Where(v => except == null || v.Id != except).Select(v => v.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<HashSet<string>> FolderNamesAsync(string? roleId, string userId, Guid? except, CancellationToken ct) =>
        (await FoldersIn(roleId, userId).Where(f => except == null || f.Id != except).Select(f => f.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first of <paramref name="candidate"/>(1), (2)… that isn't taken, kept within the
    /// name length limit.</summary>
    private static string FirstFree(string name, HashSet<string> taken, Func<string, int, string> candidate)
    {
        if (!taken.Contains(name)) return name;
        for (var n = 1; ; n++)
        {
            var next = candidate(name, n);
            if (next.Length > MaxNameLength) next = candidate(name[..Math.Max(1, name.Length - (next.Length - MaxNameLength))], n);
            if (!taken.Contains(next)) return next;
        }
    }

    private static string Numbered(string name, int n) => $"{name} {n + 1}";
    private static string Parenthesized(string name, int n) => $"{name} ({n + 1})";
    private static string CopyName(string name, int n) => n == 1
        ? Loc.T("server.view.copyName", new { name })
        : Loc.T("server.view.copyNameNumbered", new { name, number = n });

    // ── Reading ─────────────────────────────────────────────────────────────

    public async Task<ViewTreeDto> GetTreeAsync(string userId, CancellationToken ct = default)
    {
        var access = await AccessAsync(userId, ct);
        var roleIds = await VisibleRoleIdsAsync(access, ct);
        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).Select(r => new { r.Id, r.Name }).ToListAsync(ct);
        var views = await VisibleViews(userId, roleIds).AsNoTracking().ToListAsync(ct);
        var folders = await db.ViewFolders.AsNoTracking()
            .Where(f => (f.RoleId == null && f.UserId == userId) || (f.RoleId != null && roleIds.Contains(f.RoleId)))
            .ToListAsync(ct);

        ViewTreeRootDto Root(string tree, string name, string kind, bool canEdit, string? roleId) => new(tree, name, kind, canEdit,
            folders.Where(f => f.RoleId == roleId).OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new ViewFolderDto(f.Id, f.ParentId, f.Name)).ToList(),
            views.Where(v => v.RoleId == roleId).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(v => new ViewItemDto(v.Id, v.FolderId, v.Name, CountCells(v.LayoutJson), v.SequenceIntervalSeconds)).ToList());

        var trees = new List<ViewTreeRootDto> { Root(ViewTrees.Private, Loc.T("server.view.privateTree"), "private", true, null) };
        // The user's own roles (every role for a Super Admin) first, then roles they may only open.
        foreach (var role in roles.OrderBy(r => access.IsSuperAdmin || access.RoleIds.Contains(r.Id) ? 0 : 1)
                     .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var own = access.IsSuperAdmin || access.RoleIds.Contains(role.Id);
            trees.Add(Root(ViewTrees.Role(role.Id), role.Name ?? role.Id, own ? "role" : "granted",
                access.CanEdit(role.Id, userId), role.Id));
        }
        return new ViewTreeDto(trees);
    }

    public async Task<List<View>> ListVisibleToAsync(string userId, CancellationToken ct = default)
    {
        var access = await AccessAsync(userId, ct);
        var roleIds = await VisibleRoleIdsAsync(access, ct);
        return await VisibleViews(userId, roleIds).OrderBy(v => v.Name).ToListAsync(ct);
    }

    public async Task<List<(View View, string Path)>> ListVisibleWithPathAsync(string userId, CancellationToken ct = default)
    {
        var tree = await GetTreeAsync(userId, ct);
        var byId = (await ListVisibleToAsync(userId, ct)).ToDictionary(v => v.Id);
        var result = new List<(View, string)>();
        foreach (var root in tree.Trees)
        {
            var folders = root.Folders.ToDictionary(f => f.Id);
            string PathOf(Guid? folderId)
            {
                var parts = new List<string>();
                var seen = new HashSet<Guid>();
                while (folderId is { } id && folders.TryGetValue(id, out var folder) && seen.Add(id))
                {
                    parts.Insert(0, folder.Name);
                    folderId = folder.ParentId;
                }
                parts.Insert(0, root.Name);
                return string.Join(" / ", parts);
            }
            foreach (var item in root.Views)
                if (byId.TryGetValue(item.Id, out var view)) result.Add((view, PathOf(item.FolderId)));
        }
        return result;
    }

    public async Task<View?> GetVisibleToAsync(Guid id, string userId, CancellationToken ct = default)
    {
        var view = await db.Views.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (view is null) return null;
        var access = await AccessAsync(userId, ct);
        return access.CanSee(view.RoleId, view.OwnerId) ? view : null;
    }

    public async Task<bool> CanEditAsync(Guid id, string userId, CancellationToken ct = default)
    {
        var view = await db.Views.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
        return view is not null && (await AccessAsync(userId, ct)).CanEdit(view.RoleId, view.OwnerId);
    }

    public async Task<List<View>> GetTourViewsAsync(string userId, CancellationToken ct = default) =>
        (await ListVisibleToAsync(userId, ct)).Where(v => v.SequenceIntervalSeconds > 0).ToList();

    // ── Views ───────────────────────────────────────────────────────────────

    private async Task<(View View, Access Access)> EditableViewAsync(Guid id, string userId, CancellationToken ct)
    {
        var view = await db.Views.FirstOrDefaultAsync(v => v.Id == id, ct)
            ?? throw new ViewTreeException(Loc.T("server.view.viewNotFound"));
        var access = await AccessAsync(userId, ct);
        if (!access.CanSee(view.RoleId, view.OwnerId)) throw new ViewTreeException(Loc.T("server.view.viewNotFound"));
        if (!access.CanEdit(view.RoleId, view.OwnerId)) throw new UnauthorizedAccessException(Loc.T("server.view.cantChangeThere"));
        return (view, access);
    }

    public async Task<View> CreateAsync(string name, string userId, string tree = ViewTrees.Private, Guid? folderId = null,
        CancellationToken ct = default)
    {
        var (_, roleId) = await EditableTreeAsync(userId, tree, ct);
        await EnsureFolderInTreeAsync(folderId, roleId, userId, ct);
        var clean = FirstFree(CleanName(name), await ViewNamesAsync(roleId, userId, null, ct), Numbered);
        var view = new View
        {
            Id = Guid.NewGuid(), Name = clean, OwnerId = userId, RoleId = roleId, FolderId = folderId, CreatedAt = DateTime.UtcNow,
        };
        db.Views.Add(view);
        await db.SaveChangesAsync(ct);
        return view;
    }

    public async Task UpdateAsync(Guid id, string userId, string name, string layoutJson, int sequenceIntervalSeconds,
        CancellationToken ct = default)
    {
        var (view, _) = await EditableViewAsync(id, userId, ct);
        var clean = CleanName(name);
        if ((await ViewNamesAsync(view.RoleId, TreeOwner(view), view.Id, ct)).Contains(clean))
            throw new ViewTreeException(Loc.T("server.view.viewNameTaken", new { name = clean }));
        view.Name = clean;
        view.LayoutJson = layoutJson;
        view.SequenceIntervalSeconds = Math.Max(0, sequenceIntervalSeconds);
        view.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RenameAsync(Guid id, string userId, string name, CancellationToken ct = default)
    {
        var (view, _) = await EditableViewAsync(id, userId, ct);
        var clean = CleanName(name);
        if ((await ViewNamesAsync(view.RoleId, TreeOwner(view), view.Id, ct)).Contains(clean))
            throw new ViewTreeException(Loc.T("server.view.viewNameTaken", new { name = clean }));
        view.Name = clean;
        view.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<View> CopyAsync(Guid id, string userId, string targetTree, Guid? targetFolderId, CancellationToken ct = default)
    {
        var source = await GetVisibleToAsync(id, userId, ct) ?? throw new ViewTreeException(Loc.T("server.view.viewNotFound"));
        var (_, roleId) = await EditableTreeAsync(userId, targetTree, ct);
        await EnsureFolderInTreeAsync(targetFolderId, roleId, userId, ct);
        var copy = new View
        {
            Id = Guid.NewGuid(),
            Name = FirstFree(source.Name, await ViewNamesAsync(roleId, userId, null, ct), CopyName),
            OwnerId = userId,
            RoleId = roleId,
            FolderId = targetFolderId,
            LayoutJson = source.LayoutJson,
            SequenceIntervalSeconds = source.SequenceIntervalSeconds,
            CreatedAt = DateTime.UtcNow,
        };
        db.Views.Add(copy);
        await db.SaveChangesAsync(ct);
        return copy;
    }

    public async Task MoveAsync(Guid id, string userId, string targetTree, Guid? targetFolderId, CancellationToken ct = default)
    {
        var (view, _) = await EditableViewAsync(id, userId, ct);
        var (_, roleId) = await EditableTreeAsync(userId, targetTree, ct);
        await EnsureFolderInTreeAsync(targetFolderId, roleId, userId, ct);

        var sameTree = view.RoleId == roleId && (roleId is not null || view.OwnerId == userId);
        if (!sameTree)
        {
            if ((await ViewNamesAsync(roleId, userId, null, ct)).Contains(view.Name))
                throw new ViewTreeException(Loc.T("server.view.viewNameTaken", new { name = view.Name }));
            view.RoleId = roleId;
            // A view moved into a Private tree belongs to that tree's user.
            if (roleId is null) view.OwnerId = userId;
        }
        view.FolderId = targetFolderId;
        view.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, string userId, CancellationToken ct = default)
    {
        var (view, _) = await EditableViewAsync(id, userId, ct);
        db.Views.Remove(view);
        await db.SaveChangesAsync(ct);
    }

    // ── Folders ─────────────────────────────────────────────────────────────

    private async Task<ViewFolder> EditableFolderAsync(Guid id, string userId, CancellationToken ct)
    {
        var folder = await db.ViewFolders.FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw new ViewTreeException(Loc.T("server.view.folderGone"));
        var access = await AccessAsync(userId, ct);
        if (!access.CanSee(folder.RoleId, TreeOwner(folder))) throw new ViewTreeException(Loc.T("server.view.folderGone"));
        if (!access.CanEdit(folder.RoleId, TreeOwner(folder))) throw new UnauthorizedAccessException(Loc.T("server.view.cantChangeThere"));
        return folder;
    }

    public async Task<ViewFolder> CreateFolderAsync(string userId, string tree, Guid? parentId, string name, CancellationToken ct = default)
    {
        var (_, roleId) = await EditableTreeAsync(userId, tree, ct);
        await EnsureFolderInTreeAsync(parentId, roleId, userId, ct);
        var clean = FirstFree(CleanName(name), await FolderNamesAsync(roleId, userId, null, ct), Numbered);
        var folder = new ViewFolder
        {
            Id = Guid.NewGuid(), ParentId = parentId, RoleId = roleId, UserId = roleId is null ? userId : null,
            Name = clean, CreatedAt = DateTime.UtcNow, CreatedBy = userId,
        };
        db.ViewFolders.Add(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public async Task RenameFolderAsync(Guid id, string userId, string name, CancellationToken ct = default)
    {
        var folder = await EditableFolderAsync(id, userId, ct);
        var clean = CleanName(name);
        if ((await FolderNamesAsync(folder.RoleId, TreeOwner(folder), folder.Id, ct)).Contains(clean))
            throw new ViewTreeException(Loc.T("server.view.folderNameTaken", new { name = clean }));
        folder.Name = clean;
        await db.SaveChangesAsync(ct);
    }

    public async Task MoveFolderAsync(Guid id, string userId, string targetTree, Guid? targetParentId, CancellationToken ct = default)
    {
        var folder = await EditableFolderAsync(id, userId, ct);
        var (_, roleId) = await EditableTreeAsync(userId, targetTree, ct);
        await EnsureFolderInTreeAsync(targetParentId, roleId, userId, ct);

        // The folder and everything under it, within its own tree.
        var sourceFolders = await FoldersIn(folder.RoleId, TreeOwner(folder)).ToListAsync(ct);
        var children = sourceFolders.ToLookup(f => f.ParentId);
        var subtree = new List<ViewFolder>();
        var queue = new Queue<ViewFolder>([folder]);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            subtree.Add(next);
            foreach (var child in children[next.Id]) queue.Enqueue(child);
        }
        if (targetParentId is { } parent && subtree.Any(f => f.Id == parent))
            throw new ViewTreeException(Loc.T("server.view.folderIntoItself"));

        var sameTree = folder.RoleId == roleId && (roleId is not null || folder.UserId == userId);
        if (!sameTree)
        {
            var subtreeIds = subtree.Select(f => f.Id).ToHashSet();
            var views = await db.Views.Where(v => v.FolderId != null && subtreeIds.Contains(v.FolderId.Value)).ToListAsync(ct);
            var takenFolders = await FolderNamesAsync(roleId, userId, null, ct);
            var takenViews = await ViewNamesAsync(roleId, userId, null, ct);
            var clash = subtree.Select(f => f.Name).FirstOrDefault(takenFolders.Contains);
            if (clash is not null) throw new ViewTreeException(Loc.T("server.view.folderNameTaken", new { name = clash }));
            clash = views.Select(v => v.Name).FirstOrDefault(takenViews.Contains);
            if (clash is not null) throw new ViewTreeException(Loc.T("server.view.viewNameTaken", new { name = clash }));

            foreach (var f in subtree)
            {
                f.RoleId = roleId;
                f.UserId = roleId is null ? userId : null;
            }
            foreach (var v in views)
            {
                v.RoleId = roleId;
                if (roleId is null) v.OwnerId = userId;
                v.UpdatedAt = DateTime.UtcNow;
            }
        }
        folder.ParentId = targetParentId;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteFolderAsync(Guid id, string userId, CancellationToken ct = default)
    {
        var folder = await EditableFolderAsync(id, userId, ct);
        // What's inside moves up a level rather than being lost.
        foreach (var child in await db.ViewFolders.Where(f => f.ParentId == id).ToListAsync(ct))
            child.ParentId = folder.ParentId;
        foreach (var view in await db.Views.Where(v => v.FolderId == id).ToListAsync(ct))
            view.FolderId = folder.ParentId;
        db.ViewFolders.Remove(folder);
        await db.SaveChangesAsync(ct);
    }

    // ── Upkeep ──────────────────────────────────────────────────────────────

    public async Task MoveDeletedRoleTreeAsync(string roleId, string toUserId, CancellationToken ct = default)
    {
        var takenViews = await ViewNamesAsync(null, toUserId, null, ct);
        foreach (var view in await db.Views.Where(v => v.RoleId == roleId).ToListAsync(ct))
        {
            view.Name = FirstFree(view.Name, takenViews, Parenthesized);
            takenViews.Add(view.Name);
            view.RoleId = null;
            view.OwnerId = toUserId;
        }
        var takenFolders = await FolderNamesAsync(null, toUserId, null, ct);
        foreach (var folder in await db.ViewFolders.Where(f => f.RoleId == roleId).ToListAsync(ct))
        {
            folder.Name = FirstFree(folder.Name, takenFolders, Parenthesized);
            takenFolders.Add(folder.Name);
            folder.RoleId = null;
            folder.UserId = toUserId;
        }
        db.ViewTreeGrants.RemoveRange(await db.ViewTreeGrants.Where(g => g.OwnerRoleId == roleId || g.GrantedRoleId == roleId).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> MakeNamesUniqueAsync(CancellationToken ct = default)
    {
        var renamed = 0;
        foreach (var tree in (await db.Views.ToListAsync(ct)).GroupBy(v => v.RoleId ?? "user:" + v.OwnerId))
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in tree.OrderBy(v => v.CreatedAt).ThenBy(v => v.Id))
            {
                if (!taken.Add(view.Name))
                {
                    view.Name = FirstFree(view.Name, taken, Parenthesized);
                    taken.Add(view.Name);
                    renamed++;
                }
            }
        }
        foreach (var tree in (await db.ViewFolders.ToListAsync(ct)).GroupBy(f => f.RoleId ?? "user:" + f.UserId))
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in tree.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id))
            {
                if (!taken.Add(folder.Name))
                {
                    folder.Name = FirstFree(folder.Name, taken, Parenthesized);
                    taken.Add(folder.Name);
                    renamed++;
                }
            }
        }
        if (renamed > 0) await db.SaveChangesAsync(ct);
        return renamed;
    }

    private static int CountCells(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(layoutJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array
                ? cells.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}

/// <inheritdoc cref="IViewAccessService" />
public class ViewAccessService(ApplicationDbContext db) : IViewAccessService
{
    public Task<List<ViewTreeGrant>> ListAsync(CancellationToken ct = default) =>
        db.ViewTreeGrants.AsNoTracking().ToListAsync(ct);

    public async Task GrantAsync(string ownerRoleId, string grantedRoleId, string? by, CancellationToken ct = default)
    {
        if (ownerRoleId == grantedRoleId) return; // a role always sees its own views
        if (await db.Roles.CountAsync(r => r.Id == ownerRoleId || r.Id == grantedRoleId, ct) != 2)
            throw new ViewTreeException(Loc.T("server.view.roleGone"));
        if (await db.ViewTreeGrants.AnyAsync(g => g.OwnerRoleId == ownerRoleId && g.GrantedRoleId == grantedRoleId, ct)) return;
        db.ViewTreeGrants.Add(new ViewTreeGrant
        {
            Id = Guid.NewGuid(), OwnerRoleId = ownerRoleId, GrantedRoleId = grantedRoleId, CreatedAt = DateTime.UtcNow, CreatedBy = by,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(string ownerRoleId, string grantedRoleId, CancellationToken ct = default)
    {
        var grant = await db.ViewTreeGrants.FirstOrDefaultAsync(g => g.OwnerRoleId == ownerRoleId && g.GrantedRoleId == grantedRoleId, ct);
        if (grant is null) return;
        db.ViewTreeGrants.Remove(grant);
        await db.SaveChangesAsync(ct);
    }
}
