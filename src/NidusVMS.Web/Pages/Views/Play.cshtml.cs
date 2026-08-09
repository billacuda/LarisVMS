using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;

namespace NidusVMS.Web.Pages.Views;

[Authorize("Views.View")]
public class PlayModel(IViewService viewService, ICameraService cameraService) : PageModel
{
    public string Name { get; set; } = string.Empty;
    public string LayoutJson { get; set; } = "{\"cells\":[],\"mobileTwoColumn\":false}";
    public List<Camera> Cameras { get; set; } = [];

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

        var all = await cameraService.ListAsync();
        Cameras = all.Where(c => c.IsEnabled && c.NodeId is not null).ToList();

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
