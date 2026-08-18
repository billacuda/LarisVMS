using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LarisVMS.Web.Pages.Live;

/// <summary>Pure redirect page — Views are now the only live-viewing surface (the old flat
/// all-cameras grid was removed; a user who wants "all cameras" creates a View containing every
/// camera). Sends the viewer straight to the last-watched view, falling back to the first view, or a
/// "create one" prompt when none exist yet.
///
/// The redirect itself now happens server-side, reading the "lastViewId" user preference (M14) —
/// previously a client-side redirect reading localStorage, which meant the choice was per-browser
/// and reset on a new device. Server-side also means Views.Count == 0 never briefly flashes this
/// page's own markup before a client script fires; there's no client script left to fire.</summary>
[Authorize("Cameras.View")]
public class IndexModel(IViewService viewService, IUserPreferenceService preferences) : PageModel
{
    public List<View> Views { get; set; } = [];

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

        return RedirectToPage("/Views/Play", new { id = target });
    }
}
