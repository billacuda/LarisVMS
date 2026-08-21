namespace LarisVMS.Web.Helpers;

/// <summary>
/// Which page numbers a pagination control should render, for a page count too large to list in
/// full — confirmed live on Snapshots as literally hundreds of page links in one row. Explicit user
/// ask: show the first 10 and last 10 pages always, plus enough around the current page that its
/// position stays visible even when it falls between those two edges (the common case on a genuinely
/// large result set) — everything else collapses into an ellipsis gap. Small total page counts still
/// render every page with no gaps at all, since the three windows simply overlap into one run.
/// </summary>
public static class PaginationWindow
{
    /// <summary>Page numbers to render, in order; <c>null</c> marks an ellipsis gap between two
    /// non-adjacent numbers (never two gaps in a row — an adjacent-but-not-touching pair of windows
    /// collapses to just showing the number between them instead).</summary>
    public static List<int?> Compute(int currentPage, int totalPages, int edgeCount = 10, int aroundCurrent = 2)
    {
        if (totalPages <= 0) return [];
        currentPage = Math.Clamp(currentPage, 1, totalPages);

        var keep = new SortedSet<int>();
        for (var p = 1; p <= Math.Min(edgeCount, totalPages); p++) keep.Add(p);
        for (var p = Math.Max(1, totalPages - edgeCount + 1); p <= totalPages; p++) keep.Add(p);
        for (var p = Math.Max(1, currentPage - aroundCurrent); p <= Math.Min(totalPages, currentPage + aroundCurrent); p++) keep.Add(p);

        var result = new List<int?>();
        int? prev = null;
        foreach (var p in keep)
        {
            // A gap of exactly one page (e.g. edge window ends at 10, current window starts at 12)
            // would render an ellipsis standing in for a single real page — worse than just showing
            // that page, so it's included outright instead of collapsed.
            if (prev is not null && p == prev + 2) result.Add(prev + 1);
            else if (prev is not null && p > prev + 1) result.Add(null);
            result.Add(p);
            prev = p;
        }
        return result;
    }
}
