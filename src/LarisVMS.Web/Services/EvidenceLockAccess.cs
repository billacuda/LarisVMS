using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>Removing or restoring an evidence lock: besides being its creator (EvidenceLockService),
/// the person still needs to be allowed to lock footage and to play back that camera — someone who
/// has since lost access can't release footage they placed a lock on. A Super Admin may always.</summary>
public static class EvidenceLockAccess
{
    /// <summary>Null when allowed, otherwise why not. An unknown lock is left to the service (404).</summary>
    public static async Task<string?> ManageForbiddenAsync(ClaimsPrincipal user, Guid lockId, ApplicationDbContext db,
        IPermissionService permissions, ICameraAccessService cameraAccess, CancellationToken ct = default)
    {
        var granted = await permissions.GetGrantedAsync(user, ct);
        if (granted.IsAdministrator) return null;
        if (!granted.Has("EvidenceLocks", "Create")) return "You don't have permission to manage evidence locks.";
        var cameraId = await db.EvidenceLocks.AsNoTracking().Where(l => l.Id == lockId).Select(l => (Guid?)l.CameraId).FirstOrDefaultAsync(ct);
        if (cameraId is null) return null;
        var playable = await cameraAccess.GetAccessibleCameraIdsAsync(user, CameraAccessActions.Playback, ct);
        return playable is null || playable.Contains(cameraId.Value) ? null : "You don't have playback access to this camera.";
    }
}
