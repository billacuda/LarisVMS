using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Views;

/// <summary>The view tree: Private views, a folder per role, and roles' views an admin let this
/// person open. The tree itself is drawn and changed by view-tree.js through /api/views
/// (ViewTreeApi); every rule about who may change what is ViewService's.</summary>
[Authorize("Views.View")]
public class IndexModel(IViewService viewService) : PageModel
{
    public int TourViewCount { get; set; }

    public async Task OnGetAsync()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        TourViewCount = (await viewService.GetTourViewsAsync(userId)).Count;
    }
}
