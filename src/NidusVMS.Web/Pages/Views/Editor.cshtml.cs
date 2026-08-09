using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Views;

[Authorize("Views.Edit")]
public class EditorModel(IViewService viewService, ICameraService cameraService) : PageModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsShared { get; set; }
    public string LayoutJson { get; set; } = "{\"cells\":[],\"mobileTwoColumn\":false}";
    public int SequenceIntervalSeconds { get; set; }
    public string? ErrorMessage { get; set; }

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
        IsShared = view.IsShared;
        LayoutJson = view.LayoutJson;
        SequenceIntervalSeconds = view.SequenceIntervalSeconds;

        await LoadCamerasAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, string name, bool isShared, string layoutJson, int sequenceIntervalSeconds)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        try
        {
            await viewService.UpdateAsync(id, userId, name, isShared, layoutJson, sequenceIntervalSeconds);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Id = id;
            Name = name;
            IsShared = isShared;
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
        Cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).OrderBy(c => c.Name).ToList();
    }
}
