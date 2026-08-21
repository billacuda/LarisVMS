using LarisVMS.Web.Helpers;

namespace LarisVMS.Tests;

public class PaginationWindowTests
{
    [Fact]
    public void ZeroOrFewerTotalPagesProducesNoPages()
    {
        Assert.Empty(PaginationWindow.Compute(1, 0));
    }

    [Fact]
    public void SmallTotalRendersEveryPageWithNoGaps()
    {
        // Every window (first 10, last 10, around-current) overlaps entirely, so this must collapse
        // to a plain 1..N run with zero ellipsis markers — the common case, and it must not regress
        // into showing gaps for a result set that never needed windowing at all.
        var pages = PaginationWindow.Compute(3, 7);

        Assert.Equal([1, 2, 3, 4, 5, 6, 7], pages);
    }

    [Fact]
    public void LargeTotalWithCurrentPageInTheMiddleShowsBothEdgesAndACurrentWindow()
    {
        var pages = PaginationWindow.Compute(50, 100);

        // First 10.
        Assert.Equal(Enumerable.Range(1, 10).Cast<int?>(), pages.Take(10));
        // A gap, then the current page's own small window.
        Assert.Contains(null, pages);
        Assert.Contains(48, pages);
        Assert.Contains(50, pages);
        Assert.Contains(52, pages);
        // Last 10.
        Assert.Equal(Enumerable.Range(91, 10).Cast<int?>(), pages.TakeLast(10));
    }

    [Fact]
    public void NeverRendersTwoGapsInARow()
    {
        for (var current = 1; current <= 200; current++)
        {
            var pages = PaginationWindow.Compute(current, 200);
            for (var i = 1; i < pages.Count; i++)
            {
                Assert.False(pages[i] is null && pages[i - 1] is null,
                    $"consecutive gaps at current={current}, index={i}");
            }
        }
    }

    [Fact]
    public void ASingleSkippedPageIsShownRatherThanCollapsedIntoAGap()
    {
        // Edge window (1..10) and current-window (12..16, from currentPage=14 +/-2) are separated by
        // exactly one real page (11) — an ellipsis standing in for one page is worse than just
        // showing it, so 11 must appear as a real link, and there must be no gap marker at all
        // between the two windows.
        var pages = PaginationWindow.Compute(14, 100);
        var elevenIndex = pages.IndexOf(11);

        Assert.True(elevenIndex > 0, "11 should be rendered as a real page number");
        Assert.NotNull(pages[elevenIndex - 1]); // the page right before 11 (10) is not a gap marker
        Assert.Equal(10, pages[elevenIndex - 1]);
    }

    [Fact]
    public void CurrentPageIsClampedIntoRange()
    {
        // A stale/out-of-range page number (e.g. a bookmarked page= URL from before a filter reduced
        // the result count) must not throw or produce an out-of-bounds window.
        var pages = PaginationWindow.Compute(9999, 5);

        Assert.Equal([1, 2, 3, 4, 5], pages);
    }

    [Fact]
    public void OnePageProducesNoControlAtAllViaTotalPagesCheck()
    {
        // Compute itself still returns [1] for a single page — it's the partial's own
        // "TotalPages > 1" guard that decides whether to render anything, not this method suppressing
        // a trivial case. Documented here so that guard doesn't get "simplified" away by mistake.
        Assert.Equal([1], PaginationWindow.Compute(1, 1));
    }
}
