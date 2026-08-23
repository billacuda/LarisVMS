using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
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

    /// <summary>Every camera, loaded once per request — the "manage this group's cameras" popup and
    /// the per-group member list both read from this rather than each issuing their own query.</summary>
    public List<Camera> Cameras { get; set; } = [];

    // TempData, not plain properties: every handler on this page ends in RedirectToPage (so the
    // camera-membership changes below are never resubmitted by a page refresh), and a plain property
    // doesn't survive a redirect — the page model is recreated fresh on the following GET. Matches
    // Admin/Nodes.cshtml.cs's own StatusMessage/StatusIsError convention for the same reason.
    [TempData] public string? ErrorMessage { get; set; }
    [TempData] public string? SavedMessage { get; set; }

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

    /// <summary>Sets exactly which cameras belong to one group — the "Manage cameras" popup's Save
    /// action (checkbox per camera, multi-select). Diffs the submitted set against current membership
    /// and, for each camera whose membership in *this* group actually changed, rewrites its full
    /// group list via SetCameraGroupsAsync (add/remove just this one group, keep its others) — a
    /// camera can belong to several groups, so this must never blindly overwrite the whole list with
    /// just what's checked here. A camera whose new set would span more than one site is skipped with
    /// its own error rather than aborting the whole batch, so an admin fixing one mistake doesn't lose
    /// every other change in the same save.</summary>
    public async Task<IActionResult> OnPostSetGroupCamerasAsync(Guid groupId, List<Guid> cameraIds, CancellationToken ct)
    {
        await LoadAsync(ct);

        var group = Groups.FirstOrDefault(g => g.Id == groupId);
        if (group is null) { ErrorMessage = "That group no longer exists."; return RedirectToPage(); }

        var currentMemberIds = Cameras.Where(c => c.Groups.Any(g => g.Id == groupId)).Select(c => c.Id).ToHashSet();
        var targetMemberIds = cameraIds.ToHashSet();
        var changedCameraIds = targetMemberIds.Except(currentMemberIds).Concat(currentMemberIds.Except(targetMemberIds)).ToList();

        var errors = new List<string>();
        var changedCount = 0;

        foreach (var cameraId in changedCameraIds)
        {
            var camera = Cameras.FirstOrDefault(c => c.Id == cameraId);
            if (camera is null) continue;

            var newGroupIds = camera.Groups.Select(g => g.Id).ToHashSet();
            var adding = targetMemberIds.Contains(cameraId);
            if (adding) newGroupIds.Add(groupId); else newGroupIds.Remove(groupId);

            try
            {
                await cameraService.SetCameraGroupsAsync(cameraId, newGroupIds.ToList(), ct);
                changedCount++;
                await auditService.LogAsync("Camera.Update", CurrentUserId, CurrentUserName, RemoteIp,
                    $"{camera.Name} ({camera.Id}) — Group: {(adding ? "added" : "removed")} \"{group.Name}\"", ct);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"{camera.Name}: {ex.Message}");
            }
        }

        if (errors.Count > 0) ErrorMessage = string.Join(" ", errors);
        if (changedCount > 0) SavedMessage = $"Updated \"{group.Name}\" — {changedCount} camera(s) changed.";
        return RedirectToPage();
    }

    /// <summary>Quick single-camera removal from the group card's own member list, without opening
    /// the full "Manage cameras" popup — keeps every other group membership that camera has.</summary>
    public async Task<IActionResult> OnPostRemoveCameraAsync(Guid cameraId, Guid groupId, CancellationToken ct)
    {
        await LoadAsync(ct);
        var camera = Cameras.FirstOrDefault(c => c.Id == cameraId);
        if (camera is null) { ErrorMessage = "That camera no longer exists."; return RedirectToPage(); }

        var newGroupIds = camera.Groups.Select(g => g.Id).Where(id => id != groupId).ToList();
        await cameraService.SetCameraGroupsAsync(cameraId, newGroupIds, ct);
        await auditService.LogAsync("Camera.Update", CurrentUserId, CurrentUserName, RemoteIp,
            $"{camera.Name} ({camera.Id}) — Group: removed", ct);

        SavedMessage = $"\"{camera.Name}\" removed from its group.";
        return RedirectToPage();
    }

    private string? CurrentUserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    private string? CurrentUserName => User.Identity?.Name;
    private string? RemoteIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    private async Task LoadAsync(CancellationToken ct = default)
    {
        Groups = await groupService.GetTreeAsync();
        Cameras = (await cameraService.ListAsync(ct)).OrderBy(c => c.Name).ToList();
    }
}
