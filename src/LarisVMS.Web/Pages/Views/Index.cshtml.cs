using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Views;

[Authorize("Views.View")]
public class IndexModel(IViewService viewService) : PageModel
{
    public List<View> Views { get; set; } = [];
    public List<View> TourViews { get; set; } = [];
    public string? CurrentUserId { get; set; }
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        CurrentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        Views = await viewService.ListVisibleToAsync(CurrentUserId ?? string.Empty);
        TourViews = await viewService.GetTourViewsAsync();
    }

    // Razor Pages [Authorize] only applies at the page/model level, not per-handler (MVC1001) —
    // same reasoning as Admin/Nodes: create/delete stay under the page-level Views.View policy
    // rather than requiring a separate per-handler Views.Edit check, matching how Cameras/Index's
    // Probe handler works under Cameras.View. Ownership is still enforced in ViewService.
    public async Task<IActionResult> OnPostCreateAsync(string name)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        var view = await viewService.CreateAsync(string.IsNullOrWhiteSpace(name) ? "New view" : name, userId);
        return RedirectToPage("Editor", new { id = view.Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        try
        {
            await viewService.DeleteAsync(id, userId);
        }
        catch (UnauthorizedAccessException ex)
        {
            ErrorMessage = ex.Message;
            await OnGetAsync();
            return Page();
        }
        return RedirectToPage();
    }
}
