using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Helpers;

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
///
/// Roles/permissions overhaul, pass 3: viewing stays gated by Logs.View (unchanged); OnGetExportAsync
/// additionally requires Logs.Export, checked inline since Razor Pages [Authorize] is page-level
/// only — matches permission_matrix.txt's "Export audit logs" row, which several roles hold without
/// the coarser "View audit logs" full/limited distinction mattering (e.g. Security Manager gets
/// export in full even though its own View access collapses limited-to-full already).
/// </summary>
[Authorize("Logs.View")]
public class AuditLogsModel(ApplicationDbContext db, IAuthorizationService authorizationService) : PageModel
{
    public List<AuditLog> Logs { get; set; } = [];
    public int CurrentPage { get; set; } = 1;
    public int TotalPages { get; set; }
    public string? Query { get; set; }
    public string? ActorFilter { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public bool CanExport { get; set; }

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
        CanExport = (await authorizationService.AuthorizeAsync(User, "Logs.Export")).Succeeded;

        var query = FilteredQuery(q, actor, from, to);

        var total = await query.CountAsync();
        TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        Logs = await query.OrderByDescending(l => l.OccurredAt)
            .Skip((CurrentPage - 1) * PageSize).Take(PageSize).ToListAsync();
    }

    /// <summary>Same filters as OnGetAsync, unpaginated, as a CSV download — whatever's currently
    /// filtered on screen, not just the visible page of results.</summary>
    public async Task<IActionResult> OnGetExportAsync(string? q, string? actor, string? from, string? to)
    {
        if (!(await authorizationService.AuthorizeAsync(User, "Logs.Export")).Succeeded) return Forbid();

        var logs = await FilteredQuery(q, actor, from, to).OrderByDescending(l => l.OccurredAt).ToListAsync();

        var csv = new StringBuilder("Time (UTC),Action,User,IP Address,Details\n");
        foreach (var log in logs)
        {
            csv.Append(CsvField(log.OccurredAt.ToString("O"))).Append(',')
                .Append(CsvField(log.Action)).Append(',')
                .Append(CsvField(log.UserName)).Append(',')
                .Append(CsvField(log.IpAddress)).Append(',')
                .Append(CsvField(log.Details)).Append('\n');
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv",
            $"audit-log-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
    }

    private static string CsvField(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    private IQueryable<AuditLog> FilteredQuery(string? q, string? actor, string? from, string? to)
    {
        var query = db.AuditLogs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(l => l.Action.Contains(q) || (l.Details != null && l.Details.Contains(q)));

        if (!string.IsNullOrWhiteSpace(actor))
            query = query.Where(l => l.UserName != null && l.UserName.Contains(actor));

        // OccurredAt is UTC, while these come from a date picker that means a *local* day — see
        // LocalDateFilter for the offset bug this fixes (a selected day was cut short by the UTC
        // offset, which read as "the filter stops mid-afternoon").
        if (LocalDateFilter.StartOfDayUtc(from) is { } fromUtc)
            query = query.Where(l => l.OccurredAt >= fromUtc);

        if (LocalDateFilter.EndOfDayUtc(to) is { } toUtc)
            query = query.Where(l => l.OccurredAt <= toUtc);

        return query;
    }
}
