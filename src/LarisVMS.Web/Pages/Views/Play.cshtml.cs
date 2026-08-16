using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Helpers;

namespace LarisVMS.Web.Pages.Views;

[Authorize("Views.View")]
public class PlayModel(IViewService viewService, ICameraService cameraService, IAuditService auditService) : PageModel
{
    public string Name { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = "{\"cells\":[],\"mobileTwoColumn\":false}";
    public List<Camera> Cameras { get; set; } = [];
    public Guid CurrentViewId { get; set; }

    /// <summary>All other views the current user can switch to without leaving this page — the
    /// picker itself lives only on Live/Index (M6), so once a user landed here that was the only
    /// way to reach another view was "Back to views" + pick again; this mirrors that same picker
    /// here so switching views doesn't require leaving live playback.</summary>
    public List<View> Views { get; set; } = [];

    public bool IsTour { get; set; }
    public List<Guid> TourViewIds { get; set; } = [];
    public int TourIndex { get; set; }
    public int TourIntervalSeconds { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, bool tour, int i)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

        if (id is null)
        {
            if (!tour) return RedirectToPage("Index");
            var tourViews = await viewService.GetTourViewsAsync();
            if (tourViews.Count == 0) return RedirectToPage("Index");
            return RedirectToPage(new { id = tourViews[0].Id, tour = true, i = 0 });
        }

        var view = await viewService.GetVisibleToAsync(id.Value, userId);
        if (view is null) return RedirectToPage("Index");

        Name = view.Name;
        LayoutJson = view.LayoutJson;
        CurrentViewId = view.Id;
        Views = await viewService.ListVisibleToAsync(userId);

        var all = await cameraService.ListAsync();
        Cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).ToList();

        // Names resolved from the view's own layout, not from the Cameras list above — that list is
        // every enabled camera on the system (it feeds the client-side player), not this view's own
        // set, so using it would log every camera in the deployment on every view opened.
        var viewCameraNames = ViewLayout.CameraIds(view.LayoutJson)
            .Select(cameraId => all.FirstOrDefault(c => c.Id == cameraId)?.Name ?? cameraId.ToString())
            .ToList();
        await auditService.LogAsync("View.Watch", userId, User.Identity?.Name,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            viewCameraNames.Count == 0
                ? $"View '{view.Name}' (no cameras)"
                : $"View '{view.Name}': {string.Join(", ", viewCameraNames)}");

        IsTour = tour;
        if (tour)
        {
            TourViewIds = (await viewService.GetTourViewsAsync()).Select(v => v.Id).ToList();
            TourIndex = i;
            // A tour stop with no interval of its own (e.g. reached via a direct link rather than
            // GetTourViewsAsync, which only returns views that already have one set) still needs to
            // advance eventually rather than sitting on this view forever.
            TourIntervalSeconds = view.SequenceIntervalSeconds > 0 ? view.SequenceIntervalSeconds : 15;
        }

        return Page();
    }
}
