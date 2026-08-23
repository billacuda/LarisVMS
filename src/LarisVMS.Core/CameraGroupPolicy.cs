using LarisVMS.Core.Entities;

namespace LarisVMS.Core;

/// <summary>
/// A camera can belong to any number of CameraGroups, but all of them must share the same top-level
/// ancestor (Site) — a camera belongs to exactly one Site, and any number of groups/sub-groups beneath
/// it. Enforced here at the application layer, not the schema, since CameraGroup's self-referencing
/// tree has no notion of "site" beyond ParentId == null. Pure and DbContext-free so CameraService can
/// check a candidate group set before writing it, without needing a real database to unit test the
/// rule itself.
/// </summary>
public static class CameraGroupPolicy
{
    /// <summary>The top-level ancestor's Id for a given group — itself, if it's already a site
    /// (ParentId == null). Walks Parent references, so the full ancestor chain must already be loaded
    /// (or resolved via a lookup, see the IReadOnlyDictionary overload) — CameraGroup itself doesn't
    /// eagerly load Parent by default.</summary>
    public static Guid SiteIdOf(CameraGroup group)
    {
        var current = group;
        while (current.Parent is not null) current = current.Parent;
        return current.Id;
    }

    /// <summary>Same as SiteIdOf(CameraGroup), but resolving ancestors from a flat lookup (every
    /// group's Id -> its own ParentId) instead of a loaded Parent navigation — what CameraService
    /// actually has available (a full CameraGroup tree with Parent references would be more querying
    /// than this needs). Throws only via GetValueOrDefault's own null-propagation guard if groupId
    /// isn't in the lookup at all, which callers should treat as "the group doesn't exist" upstream.</summary>
    public static Guid SiteIdOf(Guid groupId, IReadOnlyDictionary<Guid, Guid?> parentIdByGroupId)
    {
        var current = groupId;
        while (parentIdByGroupId.GetValueOrDefault(current) is { } parentId) current = parentId;
        return current;
    }

    /// <summary>True if every group in the set shares the same Site — the invariant a camera's
    /// Groups collection must always satisfy. An empty set trivially satisfies it (no groups at all is
    /// always valid, same as today's "no group" default).</summary>
    public static bool AllShareOneSite(IReadOnlyCollection<Guid> groupIds, IReadOnlyDictionary<Guid, Guid?> parentIdByGroupId) =>
        groupIds.Select(id => SiteIdOf(id, parentIdByGroupId)).Distinct().Count() <= 1;
}
