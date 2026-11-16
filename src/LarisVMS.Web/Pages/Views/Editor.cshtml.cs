using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Views;

// Views.View, not Views.Edit: anyone may edit their own Private views. Whether this view may be
// changed (its tree) is ViewService's call.
[Authorize("Views.View")]
public class EditorModel(IViewService viewService, ICameraService cameraService, ICameraAccessService cameraAccess) : PageModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = "{\"cells\":[],\"mobileTwoColumn\":false}";
    public int SequenceIntervalSeconds { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>A view in a tree this person may open but not change: shown, not saved.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Only cameras that could actually answer a /live request — same filter as
    /// Pages/Live/Index, since a cell for a camera that can't stream is dead weight in the palette.</summary>
    public List<Camera> Cameras { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        var view = await viewService.GetVisibleToAsync(id, userId);
        if (view is null) return RedirectToPage("Index");

        Id = view.Id;
        Name = view.Name;
        LayoutJson = view.LayoutJson;
        SequenceIntervalSeconds = view.SequenceIntervalSeconds;
        ReadOnly = !await viewService.CanEditAsync(view.Id, userId);

        await LoadCamerasAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, string name, string layoutJson, int sequenceIntervalSeconds)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        try
        {
            await viewService.UpdateAsync(id, userId, name, layoutJson, sequenceIntervalSeconds);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ViewTreeException)
        {
            Id = id;
            Name = name;
            LayoutJson = layoutJson;
            SequenceIntervalSeconds = sequenceIntervalSeconds;
            ErrorMessage = ex.Message;
            await LoadCamerasAsync();
            return Page();
        }
        return RedirectToPage("Editor", new { id });
    }

    private async Task LoadCamerasAsync()
    {
        var all = await cameraService.ListAsync();
        // Only cameras this person may watch.
        var viewable = await cameraAccess.GetAccessibleCameraIdsAsync(User, LarisVMS.Core.Enums.CameraAccessActions.View);
        Cameras = all.Where(c => c.IsEnabled && c.NodeId is not null && (viewable is null || viewable.Contains(c.Id)))
            .OrderBy(c => c.Name).ToList();
    }
}
