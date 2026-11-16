using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>
/// M6 saved camera-wall layouts, organised in view trees (Pages/Views/*). Every rule about who may
/// see and change which view lives here, not in the page models, so every page agrees.
///
/// <list type="bullet">
/// <item>Each user has a Private tree only they see. Changing it needs only Views.View.</item>
/// <item>Each role has a shared tree. Its members see it; members who also have Views.Edit change it.</item>
/// <item>An admin may let one role open another role's tree (<see cref="ViewTreeGrant"/>), open-only.</item>
/// <item>Super Admins see and change every role's tree, never another user's Private tree.</item>
/// <item>View names are unique within a tree, and so are folder names (case-insensitive).</item>
/// </list>
///
/// A tree is named by a string: <see cref="ViewTrees.Private"/> for the caller's own Private tree, or
/// <c>role:{roleId}</c> (<see cref="ViewTrees.Role"/>). Rule breaks throw
/// <see cref="ViewTreeException"/> (a message to show), missing rights
/// <see cref="UnauthorizedAccessException"/>.
/// </summary>
public interface IViewService
{
    /// <summary>Every tree this user can see, with their folders and views.</summary>
    Task<ViewTreeDto> GetTreeAsync(string userId, CancellationToken ct = default);

    /// <summary>Every view this user can open, ordered by name.</summary>
    Task<List<View>> ListVisibleToAsync(string userId, CancellationToken ct = default);

    /// <summary>Every view this user can open, each with its place ("Private / Folder", "Role /
    /// Folder"), ordered by place then name — for pickers that group views.</summary>
    Task<List<(View View, string Path)>> ListVisibleWithPathAsync(string userId, CancellationToken ct = default);

    /// <summary>Null if the view doesn't exist or this user can't open it.</summary>
    Task<View?> GetVisibleToAsync(Guid id, string userId, CancellationToken ct = default);

    /// <summary>Whether this user may change the view (its tree is editable for them).</summary>
    Task<bool> CanEditAsync(Guid id, string userId, CancellationToken ct = default);

    /// <summary>A new, empty view in <paramref name="tree"/> (and <paramref name="folderId"/>), named
    /// <paramref name="name"/> or, if that's taken there, the first free "name 2", "name 3"….</summary>
    Task<View> CreateAsync(string name, string userId, string tree = ViewTrees.Private, Guid? folderId = null,
        CancellationToken ct = default);

    Task UpdateAsync(Guid id, string userId, string name, string layoutJson, int sequenceIntervalSeconds,
        CancellationToken ct = default);

    Task RenameAsync(Guid id, string userId, string name, CancellationToken ct = default);

    /// <summary>An independent copy in another (or the same) tree and folder. If its name is taken
    /// there, the copy is named "{name} - copy", then "{name} - copy 2", and so on.</summary>
    Task<View> CopyAsync(Guid id, string userId, string targetTree, Guid? targetFolderId, CancellationToken ct = default);

    /// <summary>Needs the right to change both the view's tree and the target tree. Its name must be
    /// free in the target tree.</summary>
    Task MoveAsync(Guid id, string userId, string targetTree, Guid? targetFolderId, CancellationToken ct = default);

    Task DeleteAsync(Guid id, string userId, CancellationToken ct = default);

    Task<ViewFolder> CreateFolderAsync(string userId, string tree, Guid? parentId, string name, CancellationToken ct = default);
    Task RenameFolderAsync(Guid id, string userId, string name, CancellationToken ct = default);

    /// <summary>Moves a folder with everything in it. Not into itself or one of its own folders; every
    /// name in it must be free in the target tree.</summary>
    Task MoveFolderAsync(Guid id, string userId, string targetTree, Guid? targetParentId, CancellationToken ct = default);

    /// <summary>Deletes the folder only: its views and folders move up to its parent.</summary>
    Task DeleteFolderAsync(Guid id, string userId, CancellationToken ct = default);

    /// <summary>Views this user can open that have a tour interval, ordered by name — what "Start
    /// tour" cycles through.</summary>
    Task<List<View>> GetTourViewsAsync(string userId, CancellationToken ct = default);

    /// <summary>A role was deleted: its views and folders move into <paramref name="toUserId"/>'s
    /// Private tree (a name already taken there gets " (2)"), and its view grants go.</summary>
    Task MoveDeletedRoleTreeAsync(string roleId, string toUserId, CancellationToken ct = default);

    /// <summary>Makes names unique within every tree, renaming later duplicates "Name (2)" — views
    /// from before trees existed could share a name. Idempotent.</summary>
    Task<int> MakeNamesUniqueAsync(CancellationToken ct = default);
}

/// <summary>Settings → View access: which roles may open which other roles' view trees.</summary>
public interface IViewAccessService
{
    Task<List<ViewTreeGrant>> ListAsync(CancellationToken ct = default);
    Task GrantAsync(string ownerRoleId, string grantedRoleId, string? by, CancellationToken ct = default);
    Task RevokeAsync(string ownerRoleId, string grantedRoleId, CancellationToken ct = default);
}

/// <summary>Names of view trees, as the API and pages pass them.</summary>
public static class ViewTrees
{
    public const string Private = "private";
    public const string RolePrefix = "role:";

    public static string Role(string roleId) => RolePrefix + roleId;

    /// <summary>The role id of a role tree's name, or null for the Private tree. False if the name is
    /// neither.</summary>
    public static bool TryParse(string? tree, out string? roleId)
    {
        roleId = null;
        if (tree == Private) return true;
        if (tree is not null && tree.StartsWith(RolePrefix, StringComparison.Ordinal) && tree.Length > RolePrefix.Length)
        {
            roleId = tree[RolePrefix.Length..];
            return true;
        }
        return false;
    }
}

/// <summary>A rule about view trees was broken (a name already used, a folder moved into itself…).
/// The message is ready to show.</summary>
public sealed class ViewTreeException(string message) : Exception(message);

/// <summary>Everything one user can see in their view trees.</summary>
public sealed record ViewTreeDto(IReadOnlyList<ViewTreeRootDto> Trees);

/// <param name="Tree">The tree's name (<see cref="ViewTrees"/>).</param>
/// <param name="Kind">"private", "role" (a role the user is in, or any role for a Super Admin), or
/// "granted" (another role's tree an admin let one of the user's roles open).</param>
/// <param name="CanEdit">Whether the user may change anything in it.</param>
public sealed record ViewTreeRootDto(string Tree, string Name, string Kind, bool CanEdit,
    IReadOnlyList<ViewFolderDto> Folders, IReadOnlyList<ViewItemDto> Views);

public sealed record ViewFolderDto(Guid Id, Guid? ParentId, string Name);

public sealed record ViewItemDto(Guid Id, Guid? FolderId, string Name, int Cells, int SequenceIntervalSeconds);
