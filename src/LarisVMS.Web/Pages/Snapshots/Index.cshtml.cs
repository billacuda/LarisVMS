using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Web.Pages.Snapshots;

/// <summary>
/// M18: browses every motion event (any Source — zone, camera-pushed, custom tag, object detection)
/// as a grid of thumbnails, newest first. Shared across everyone holding Playback.View, same
/// visibility model as Exports/Bookmarks — no per-camera CameraAccess narrowing, matching those two.
/// Each card's image is fetched live from /playback-thumbnail (the same historical frame-extraction
/// path Playback's own hover thumbnails use) rather than anything captured or stored by this feature
/// — see SnapshotDto's own doc comment. "Play" reuses Bookmarks' own deep-link scheme
/// (?cameraId=&atUtc=) into Playback.
/// </summary>
[Authorize("Playback.View")]
public class IndexModel(ITimelineService timelineService, ICameraService cameraService) : PageModel
{
    private const int PageSize = 24;

    // Named Results, not Page: PageModel already declares its own Page() helper method, and a
    // same-named property here would silently hide it (CS0108) rather than error.
    public SnapshotPageDto Results { get; set; } = new([], 1, 1);
    public List<Camera> Cameras { get; set; } = [];
    public Guid? CameraId { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }

    /// <summary>Null means "the kinds filter was never touched" (a fresh visit, or the Clear link) —
    /// every checkbox renders checked and no additional narrowing applies, same as before this filter
    /// existed. Once the form is submitted, this is exactly whatever came back checked — including an
    /// empty array if the viewer unchecked everything, which then genuinely narrows to nothing rather
    /// than silently falling back to "show everything".</summary>
    public string[]? Kinds { get; set; }

    public IReadOnlyList<DetectionKind> DetectionKinds => DetectionDisplay.AllKinds;
    public const string CustomTagToken = TimelineService.CustomTagKindToken;

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
    public async Task OnGetAsync(Guid? cameraId, string? from, string? to, int pageNumber, string[]? kinds, CancellationToken ct)
    {
        CameraId = cameraId;
        From = from;
        To = to;
        Kinds = kinds;
        Cameras = (await cameraService.ListAsync(ct)).OrderBy(c => c.Name).ToList();

        // Day-granularity local dates, no timezone conversion — same simplification AuditLogsModel
        // already applies to its own from/to filters.
        DateTime? fromUtc = DateOnly.TryParse(from, out var fd) ? fd.ToDateTime(TimeOnly.MinValue) : null;
        DateTime? toUtc = DateOnly.TryParse(to, out var td) ? td.ToDateTime(TimeOnly.MaxValue) : null;

        Results = await timelineService.GetSnapshotsAsync(cameraId, fromUtc, toUtc, pageNumber < 1 ? 1 : pageNumber, PageSize, ct, kinds);
    }
}
