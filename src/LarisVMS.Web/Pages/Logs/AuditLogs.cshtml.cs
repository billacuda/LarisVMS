using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Pages.Logs;

/// <summary>
/// M11: the audit trail viewer — AuditLog rows already accumulate from login/logout (M1) and now
/// camera/node/settings/export actions, this page is the first place to actually read them back.
/// Filter/pagination shape ported from rsolva's own Pages/Admin/Logs.cshtml, adapted to LarisVMS's
/// narrower AuditLog (Action/Details, no rsolva-style EventType/EntityType split — extend only if a
/// real need for that split shows up here too).
///
/// Moved from Admin/Logs to its own top-level Logs area (with System Logs as the other tab) rather
/// than living inside the Admin dropdown — the old route now redirects here.
/// </summary>
[Authorize("Logs.View")]
public class AuditLogsModel(ApplicationDbContext db) : PageModel
{
    public List<AuditLog> Logs { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int TotalPages { get; set; }
    public string? Query { get; set; }
    public string? ActorFilter { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }

    private const int PageSize = 50;

    // pageNumber, not page: Razor Pages' own endpoint routing sets a route value literally named
    // "page" on every request (the relative page path, used internally to pick which compiled page
    // runs), and the composite model binder checks route values before the query string — a handler
    // parameter also named "page" finds that entry first, fails to parse it as an int, and silently
    // binds to 0, meaning pagination never advances past page 1 no matter what the URL says. Found
    // and fixed via the identical bug in Snapshots/Index.cshtml.cs, which this page's own doc comment
    // says its filter/pagination shape was ported from — same latent bug, ported right along with it.
    public async Task OnGetAsync(string? q, string? actor, string? from, string? to, int pageNumber = 1)
    {
        Query = q;
        ActorFilter = actor;
        From = from;
        To = to;
        CurrentPage = pageNumber < 1 ? 1 : pageNumber;

        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(l => l.Action.Contains(q) || (l.Details != null && l.Details.Contains(q)));

        if (!string.IsNullOrWhiteSpace(actor))
            query = query.Where(l => l.UserName != null && l.UserName.Contains(actor));

        if (!string.IsNullOrWhiteSpace(from) && DateOnly.TryParse(from, out var fromDate))
            query = query.Where(l => l.OccurredAt >= fromDate.ToDateTime(TimeOnly.MinValue));

        if (!string.IsNullOrWhiteSpace(to) && DateOnly.TryParse(to, out var toDate))
            query = query.Where(l => l.OccurredAt <= toDate.ToDateTime(TimeOnly.MaxValue));

        var total = await query.CountAsync();
        TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        Logs = await query.OrderByDescending(l => l.OccurredAt)
            .Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToListAsync();
    }
}
