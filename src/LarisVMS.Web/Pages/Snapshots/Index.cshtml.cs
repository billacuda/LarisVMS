using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Helpers;

namespace LarisVMS.Web.Pages.Snapshots;

/// <summary>
/// M18: browses every motion event (any Source — zone, camera-pushed, custom tag, object detection)
/// as a grid of thumbnails, newest first. Shared across everyone holding Playback.View, same
/// visibility model as Exports/Bookmarks — no per-camera CameraAccess narrowing, matching those two;
/// the group/view filter modes below don't add any either, to stay consistent with the single-camera
/// mode that's never had it.
/// Each card's image is fetched live from /playback-thumbnail (the same historical frame-extraction
/// path Playback's own hover thumbnails use) rather than anything captured or stored by this feature
/// — see SnapshotDto's own doc comment. "Play" reuses Bookmarks' own deep-link scheme
/// (?cameraId=&atUtc=) into Playback.
/// </summary>
[Authorize("Playback.View")]
public class IndexModel(ITimelineService timelineService, ICameraService cameraService,
    ICameraGroupService cameraGroupService, IViewService viewService) : PageModel
{
    private const int PageSize = 24;

    // Named Results, not Page: PageModel already declares its own Page() helper method, and a
    // same-named property here would silently hide it (CS0108) rather than error.
    public SnapshotPageDto Results { get; set; } = new([], 1, 1);
    public List<Camera> Cameras { get; set; } = [];
    public List<CameraGroup> Groups { get; set; } = [];
    public List<View> Views { get; set; } = [];
    public Guid? CameraId { get; set; }

    /// <summary>"single" (the historical default — a bare ?cameraId= with no mode still resolves as
    /// single-camera, see OnGetAsync), "all", "group", or "view".</summary>
    public string Mode { get; set; } = "single";
    public Guid? GroupId { get; set; }
    public Guid? ViewId { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }

    public int Depth(CameraGroup group) => group.MaterializedPath.Count(c => c == '/') - 1;

    /// <summary>Null means "the kinds filter was never touched" (a fresh visit, or the Clear link) —
    /// every checkbox renders checked and no additional narrowing applies, same as before this filter
    /// existed. Once the form is submitted, this is exactly whatever came back checked — including an
    /// empty array if the viewer unchecked everything, which then genuinely narrows to nothing rather
    /// than silently falling back to "show everything".</summary>
    public string[]? Kinds { get; set; }

    public IReadOnlyList<DetectionKind> DetectionKinds => DetectionDisplay.AllKinds;
    public const string CustomTagToken = TimelineService.CustomTagKindToken;

    /// <summary>Object detection plan decision 5: the live (small) set of AI-detection categories,
    /// fetched fresh every page load rather than a fixed list like DetectionKinds above — see
    /// ITimelineService.GetDetectedObjectCategoriesAsync's own doc comment for why. Rendered as its
    /// own row of filter checkboxes, alongside (not replacing) DetectionKinds — a "Vehicle" checkbox
    /// here and one from DetectionKinds are deliberately the same filter token (see
    /// TimelineService.GetSnapshotsAsync's kinds-filter comment), so only one checkbox for a name
    /// that appears in both lists is rendered.</summary>
    public List<DetectedObjectCategoryDto> DetectedObjectCategories { get; set; } = [];

    public bool IsKindChecked(string token) => Kinds is null || Kinds.Contains(token, StringComparer.OrdinalIgnoreCase);

    /// <summary>"12s" / "3m 05s" / "1h 02m" — no existing duration formatter elsewhere in the app to
    /// reuse; kept to two units at most, since a motion event's own span is never long enough for
    /// the third to matter.</summary>
    public static string FormatDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours}h {d.Minutes:D2}m";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes}m {d.Seconds:D2}s";
        return $"{Math.Max(0, (int)d.TotalSeconds)}s";
    }

    // Named pageNumber, not page: confirmed live as the actual cause of "pagination does nothing" —
    // Razor Pages' own endpoint routing sets a route value literally named "page" on every request
    // (the relative page path, used internally to select which compiled page runs), and the
    // composite model binder checks route values before the query string. A handler parameter also
    // named "page" finds that route-data entry first, fails to parse it as an int (it's a path
    // string), and silently binds to 0 — the query string's own page=2 is never even consulted. The
    // fix is simply not colliding with that reserved name, here and in Logs/AuditLogs.cshtml.cs
    // (same latent bug, same fix, ported once this was found).
    public async Task OnGetAsync(Guid? cameraId, string? mode, Guid? groupId, Guid? viewId,
        string? from, string? to, int pageNumber, string[]? kinds, CancellationToken ct)
    {
        CameraId = cameraId;
        GroupId = groupId;
        ViewId = viewId;
        From = from;
        To = to;
        Kinds = kinds;
        Cameras = (await cameraService.ListAsync(ct)).OrderBy(c => c.Name).ToList();
        Groups = await cameraGroupService.GetTreeAsync(ct);
        DetectedObjectCategories = await timelineService.GetDetectedObjectCategoriesAsync(ct);
        var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
        Views = await viewService.ListVisibleToAsync(currentUserId, ct);

        // mode absent (every pre-existing bookmarked/shared URL, and this page's own pagination
        // links before this filter existed) falls back on whether cameraId is present — preserves
        // every such link's exact prior behavior instead of suddenly meaning "all cameras".
        Mode = mode ?? (cameraId is null ? "all" : "single");

        List<Guid>? cameraIds = Mode switch
        {
            "all" => null,
            "group" when groupId is { } gid => await cameraGroupService.GetCameraIdsInSubtreeAsync(gid, ct),
            "view" when viewId is { } vid => await ResolveViewCameraIdsAsync(currentUserId, vid, ct),
            _ => cameraId is { } cid ? [cid] : null,
        };

        // The picked dates are local days, converted here to the UTC instants that bound them — see
        // LocalDateFilter for why passing them through unconverted silently truncated the selected
        // day at 4:59 PM in a UTC-7 deployment.
        var fromUtc = LocalDateFilter.StartOfDayUtc(from);
        var toUtc = LocalDateFilter.EndOfDayUtc(to);

        Results = await timelineService.GetSnapshotsAsync(cameraIds, fromUtc, toUtc, pageNumber < 1 ? 1 : pageNumber, PageSize, ct, kinds);
    }

    private async Task<List<Guid>> ResolveViewCameraIdsAsync(string userId, Guid viewId, CancellationToken ct)
    {
        var view = await viewService.GetVisibleToAsync(viewId, userId, ct);
        return view is null ? [] : ViewLayout.CameraIds(view.LayoutJson);
    }
}
