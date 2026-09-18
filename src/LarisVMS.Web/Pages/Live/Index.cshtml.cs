using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Pages.Views;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Live;

/// <summary>Renders the last-watched (or first) View's live grid directly — Views are the only
/// live-viewing surface (the old flat all-cameras grid was removed; a user who wants "all cameras"
/// creates a View containing every camera). Used to redirect to /Views/Play/{id} instead; changed
/// to render the same content in place (via the shared PlayViewModelBuilder) so the browser's URL
/// stays at /Live and the sidebar's "Live" item highlights correctly — a real HTTP redirect meant
/// the URL always genuinely ended up under /Views/Play, so "Views" (accurately) highlighted instead.</summary>
[Authorize("Cameras.View")]
public class IndexModel(IViewService viewService, IUserPreferenceService preferences,
    PlayViewModelBuilder playViewModelBuilder) : PageModel
{
    public List<View> Views { get; set; } = [];
    public PlayViewModel? Vm { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        Views = await viewService.ListVisibleToAsync(userId);
        if (Views.Count == 0) return Page();

        var saved = await preferences.GetAllAsync(userId, ct);
        var target = saved.TryGetValue("lastViewId", out var raw) && Guid.TryParse(raw, out var savedId)
                     && Views.Any(v => v.Id == savedId)
            ? savedId
            : Views[0].Id;

        Vm = await playViewModelBuilder.BuildAsync(target, User, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        // The saved/first view vanished between ListVisibleToAsync above and the builder's own
        // lookup (deleted concurrently, vanishingly rare) — fall back to the views list rather than
        // a dead page.
        if (Vm is null) return RedirectToPage("/Views/Index");

        return Page();
    }
}
