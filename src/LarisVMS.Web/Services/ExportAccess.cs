using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Services;

/// <summary>Who may see, download, and manage an export. Exports.View opens the Exports page; on top
/// of it, a job is listed when it's the user's own or every camera in it is one they may export, a
/// file downloads only for a camera they may export, and only the job's owner (or a Super Admin)
/// deletes or retries it.</summary>
public sealed class ExportAccess
{
    private ExportAccess(string userId, bool isAdministrator, HashSet<Guid>? exportable)
    {
        UserId = userId;
        IsAdministrator = isAdministrator;
        Exportable = exportable;
    }

    public string UserId { get; }
    public bool IsAdministrator { get; }

    /// <summary>Cameras this user may export; null = every camera.</summary>
    public HashSet<Guid>? Exportable { get; }

    public static async Task<ExportAccess> ForAsync(ClaimsPrincipal user, ICameraAccessService cameraAccess,
        IPermissionService permissions, CancellationToken ct = default)
    {
        var granted = await permissions.GetGrantedAsync(user, ct);
        var exportable = await cameraAccess.GetAccessibleCameraIdsAsync(user, CameraAccessActions.Export, ct);
        return new ExportAccess(user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty, granted.IsAdministrator, exportable);
    }

    public bool CanExportCamera(Guid cameraId) => Exportable is null || Exportable.Contains(cameraId);

    public bool CanSee(ExportJob job) =>
        IsAdministrator || IsOwner(job) || job.Items.All(i => CanExportCamera(i.CameraId));

    public bool CanManage(ExportJob job) => IsAdministrator || IsOwner(job);

    private bool IsOwner(ExportJob job) => UserId.Length > 0 && job.RequestedByUserId == UserId;

    /// <summary>The job holding <paramref name="itemId"/>, with its items; null when there's none.</summary>
    public static Task<ExportJob?> JobOfItemAsync(ApplicationDbContext db, Guid itemId, CancellationToken ct) =>
        db.ExportJobs.AsNoTracking().Include(j => j.Items).FirstOrDefaultAsync(j => j.Items.Any(i => i.Id == itemId), ct);

    public static Task<ExportJob?> JobAsync(ApplicationDbContext db, Guid jobId, CancellationToken ct) =>
        db.ExportJobs.AsNoTracking().Include(j => j.Items).FirstOrDefaultAsync(j => j.Id == jobId, ct);

    /// <summary>Null when the user may download item <paramref name="itemId"/>'s file or manifest;
    /// otherwise the result to return (404 for an unknown item, 403 when not allowed).</summary>
    public async Task<IResult?> DownloadForbiddenAsync(ApplicationDbContext db, Guid itemId, CancellationToken ct)
    {
        var job = await JobOfItemAsync(db, itemId, ct);
        if (job is null) return Results.NotFound();
        var item = job.Items.First(i => i.Id == itemId);
        return CanSee(job) && CanExportCamera(item.CameraId)
            ? null
            : Results.Problem("You don't have export access to this camera.", statusCode: StatusCodes.Status403Forbidden);
    }
}
