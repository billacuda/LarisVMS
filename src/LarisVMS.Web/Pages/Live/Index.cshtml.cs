using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Live;

/// <summary>Pure redirect page — Views are now the only live-viewing surface (the old flat
/// all-cameras grid was removed; a user who wants "all cameras" creates a View containing every
/// camera). Sends the viewer straight to the last-watched view (client-side, via
/// localStorage['larisvms.lastViewId'], recorded by view-play.js), falling back to the first view,
/// or a "create one" prompt when none exist yet.</summary>
[Authorize("Cameras.View")]
public class IndexModel(IViewService viewService) : PageModel
{
    public List<View> Views { get; set; } = [];

    public async Task OnGetAsync()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        Views = await viewService.ListVisibleToAsync(userId);
    }
}
