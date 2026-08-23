using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Cameras;

// Roles/permissions overhaul, pass 3: split off CameraGroups.Edit from Cameras.Edit — group CRUD
// (create/rename/delete a site/building/floor) is a distinct capability in the target matrix from
// editing an individual camera's own settings, and a role can hold one without the other (e.g.
// Security Manager gets camera groups but not camera add/edit).
[Authorize("CameraGroups.Edit")]
public class GroupsModel(ICameraGroupService groupService, ICameraService cameraService, IAuditService auditService) : PageModel
{
    [BindProperty] public string NewName { get; set; } = string.Empty;
    [BindProperty] public Guid? NewParentId { get; set; }

    public List<CameraGroup> Groups { get; set; } = [];

    /// <summary>Every camera, loaded once per request — assigning a camera to a group here is a
    /// small, contained write (just Camera.GroupId), not the full Cameras/Edit form, so this page
    /// doesn't need cameraService.GetAsync's richer per-camera detail, only what the membership list
    /// and the "add a camera" picker need to render.</summary>
    public List<Camera> Cameras { get; set; } = [];

    public string? ErrorMessage { get; set; }
    public string? SavedMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public int Depth(CameraGroup group) => group.MaterializedPath.Count(c => c == '/') - 1;

    public async Task<IActionResult> OnPostCreateAsync()
    {
        await groupService.CreateAsync(NewName, NewParentId);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        await LoadAsync();
        try
        {
            await groupService.DeleteAsync(id);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
        }
        return RedirectToPage();
    }

    /// <summary>Adds a camera to a group, or removes it (groupId null) — the same single-field write
    /// Cameras/Edit's own form does via ICameraService.UpdateAsync, just reachable from the group's
    /// own page instead of needing to open each camera individually. Every other field is passed back
    /// unchanged (null username/password leaves credentials alone, same convention Edit's own form
    /// relies on — see CameraService.UpdateAsync's doc comment).</summary>
    public async Task<IActionResult> OnPostAssignCameraAsync(Guid cameraId, Guid? groupId, CancellationToken ct)
    {
        await LoadAsync(ct);

        var camera = Cameras.FirstOrDefault(c => c.Id == cameraId);
        if (camera is null)
        {
            ErrorMessage = "That camera no longer exists.";
            return Page();
        }

        var oldGroupName = GroupName(camera.GroupId);
        var newGroupName = GroupName(groupId);

        await cameraService.UpdateAsync(cameraId, camera.Name, groupId, camera.NodeId, null, null,
            camera.IsEnabled, camera.QuotaBytes, ct: ct);

        var details = AuditDiff.Build(AuditDiff.Of("Group", oldGroupName, newGroupName));
        if (details is not null)
            await auditService.LogAsync("Camera.Update", CurrentUserId, CurrentUserName, RemoteIp,
                $"{camera.Name} ({camera.Id}) — {details}", ct);

        SavedMessage = groupId is null
            ? $"\"{camera.Name}\" removed from its group."
            : $"\"{camera.Name}\" added to \"{newGroupName}\".";
        return RedirectToPage();
    }

    private string GroupName(Guid? groupId) => groupId is null
        ? "(none)"
        : Groups.FirstOrDefault(g => g.Id == groupId)?.Name ?? groupId.ToString()!;

    private string? CurrentUserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    private string? CurrentUserName => User.Identity?.Name;
    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(CancellationToken ct = default)
    {
        Groups = await groupService.GetTreeAsync();
        Cameras = (await cameraService.ListAsync(ct)).OrderBy(c => c.Name).ToList();
    }
}
