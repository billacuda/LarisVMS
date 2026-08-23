using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Infrastructure.Services;

public class CameraAccessService(ApplicationDbContext db) : ICameraAccessService
{
    public async Task<HashSet<Guid>?> GetAccessibleCameraIdsAsync(ClaimsPrincipal user, CameraAccessActions action, CancellationToken ct = default)
    {
        var userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userId is null) return []; // not authenticated — sees nothing, not everything

        var roleNames = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var roleIds = roleNames.Count == 0
            ? []
            : await db.Roles.Where(r => roleNames.Contains(r.Name!)).Select(r => r.Id).ToListAsync(ct);

        // Super Admin is unrestricted, matching global RBAC's own short-circuit. Resolved by
        // RoleProfile.Tag rather than the role's (renamable) Name — see RoleSeedService.
        if (roleIds.Count > 0 && await db.RoleProfiles.AnyAsync(p => roleIds.Contains(p.RoleId) && p.Tag == RoleTags.SuperAdmin, ct))
            return null;

        var rows = await db.CameraAccesses
            .AsNoTracking()
            .Where(a =>
                (a.PrincipalType == CameraAccessPrincipalType.User && a.PrincipalId == userId) ||
                (a.PrincipalType == CameraAccessPrincipalType.Role && roleIds.Contains(a.PrincipalId)))
            .ToListAsync(ct);

        if (rows.Count == 0) return null; // nothing configured for this principal — see GetAccessibleCameraIdsAsync's own doc comment

        // Every Group-scoped row's own group Id -> that group's MaterializedPath, needed so Resolve
        // can cascade a group grant to its descendant groups (site/building/floor) — the whole reason
        // CameraGroup carries a materialized path at all. Fetched as one lookup regardless of which
        // rows actually grant `action`; Resolve itself does that filtering, since only it has both
        // each row's Actions and this path lookup together.
        var groupIds = rows.Where(r => r.ScopeType == CameraAccessScopeType.Group && r.ScopeId is not null)
            .Select(r => r.ScopeId!.Value).ToList();
        var groupPathsById = groupIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.CameraGroups.AsNoTracking().Where(g => groupIds.Contains(g.Id))
                .ToDictionaryAsync(g => g.Id, g => g.MaterializedPath, ct);

        var cameras = await db.Cameras
            .AsNoTracking()
            .Select(c => new CameraGroupInfo(c.Id, c.Group != null ? c.Group.MaterializedPath : null))
            .ToListAsync(ct);

        return Resolve(rows, action, cameras, groupPathsById);
    }

    internal readonly record struct CameraGroupInfo(Guid CameraId, string? GroupMaterializedPath);

    /// <summary>Pure resolution over already-fetched rows — unit-tested directly
    /// (CameraAccessServiceTests) without a real DbContext.</summary>
    internal static HashSet<Guid>? Resolve(IReadOnlyList<CameraAccess> rows, CameraAccessActions action,
        IReadOnlyList<CameraGroupInfo> cameras, IReadOnlyDictionary<Guid, string> groupPathsById)
    {
        var granted = ResolveGranted(rows, action, cameras, groupPathsById);
        if (action != CameraAccessActions.Export) return granted;

        // Export is a sub-permission of Playback: exporting a camera's footage requires being able
        // to play it back in the first place, so an Export grant is never wider than whatever
        // Playback already allows — a role holding "Export: all cameras" but "Playback: camera A
        // only" can still only export camera A. The admin UI has no such dependency between the two
        // checkboxes yet (Admin/Settings/CameraAccess.cshtml lists them independently), so this is
        // enforced here at resolution time regardless of how the underlying rows were configured,
        // rather than relying on grants always being created consistently.
        var playbackGranted = ResolveGranted(rows, CameraAccessActions.Playback, cameras, groupPathsById);
        if (granted is null) return playbackGranted; // Export itself unrestricted -> limited to whatever Playback allows
        if (playbackGranted is null) return granted; // Playback unrestricted -> Export's own resolved set already correct
        granted.IntersectWith(playbackGranted);
        return granted;
    }

    private static HashSet<Guid>? ResolveGranted(IReadOnlyList<CameraAccess> rows, CameraAccessActions action,
        IReadOnlyList<CameraGroupInfo> cameras, IReadOnlyDictionary<Guid, string> groupPathsById)
    {
        if (rows.Any(r => r.ScopeType == CameraAccessScopeType.All && r.Actions.HasFlag(action)))
            return null;

        var result = new HashSet<Guid>();

        foreach (var row in rows)
        {
            if (!row.Actions.HasFlag(action) || row.ScopeId is not { } scopeId) continue;
            if (row.ScopeType == CameraAccessScopeType.Camera) result.Add(scopeId);
        }

        var grantedGroupPaths = rows
            .Where(r => r.ScopeType == CameraAccessScopeType.Group && r.Actions.HasFlag(action) && r.ScopeId is not null)
            .Select(r => groupPathsById.GetValueOrDefault(r.ScopeId!.Value))
            .Where(p => p is not null)
            .ToList();
        if (grantedGroupPaths.Count == 0) return result;

        foreach (var camera in cameras)
        {
            if (camera.GroupMaterializedPath is null) continue;
            if (grantedGroupPaths.Any(p => camera.GroupMaterializedPath.StartsWith(p!, StringComparison.Ordinal)))
                result.Add(camera.CameraId);
        }

        return result;
    }
}
