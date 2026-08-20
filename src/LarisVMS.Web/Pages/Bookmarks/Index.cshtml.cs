using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Pages.Bookmarks;

/// <summary>
/// M18: lists every Bookmark, newest first, shared across every user holding Playback.View — same
/// visibility model as the Exports page. "Play" jumps to Playback at that exact camera/instant (see
/// playback-player.js's deep-link handling of ?cameraId=/atUtc= query params); a bookmark whose
/// camera no longer appears in any View the current user can see still links there, it just won't
/// find a matching cell to seek once it arrives (playback-player.js surfaces that as a status
/// message rather than failing silently).
/// </summary>
[Authorize("Playback.View")]
public class IndexModel(IBookmarkService bookmarkService) : PageModel
{
    public List<BookmarkDto> Bookmarks { get; set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Bookmarks = await bookmarkService.ListAsync(ct);
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        await bookmarkService.DeleteAsync(id, ct);
        return RedirectToPage();
    }
}
