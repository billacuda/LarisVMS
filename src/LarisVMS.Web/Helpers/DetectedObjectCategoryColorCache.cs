using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LarisVMS.Infrastructure.Data;

namespace LarisVMS.Web.Helpers;

/// <summary>
/// In-memory cache of DetectedObjectCategory.Name -&gt; ColorHex, refreshed periodically from the
/// database — object detection plan decision 6's own reasoning for why this exists here rather than
/// on Node or Vision Service: neither of those has any database access at all, so LarisVMS.Web's own
/// live-view proxy (ProxyDetectionOverlayAsync in Program.cs) is the only tier that can attach a
/// category's color to the live box-overlay feed before relaying it on to a browser.
///
/// A singleton, not scoped: the live-view proxy loop this backs runs for the duration of a whole WS
/// viewer session (potentially hours), far longer than any one request scope should live — a short
/// DI scope is opened only for the periodic refresh itself, never held across polls. A full reload
/// rather than a per-key cache-then-fetch, since the category set is small and mostly fixed in
/// practice (see DetectedObjectCategory's own doc comment) — simpler than tracking individual
/// cache-miss fetches for a table that rarely grows.
/// </summary>
public sealed class DetectedObjectCategoryColorCache(IServiceScopeFactory scopeFactory)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    // A box overlay tick that can't resolve a color (a genuinely brand-new category not yet
    // reflected in this cache's own refresh window) falls back to this rather than showing nothing
    // — matches DetectionDisplay.ColorHex's own "Other" fallback, the closest existing precedent for
    // "we know something was detected but can't say more specifically how to color it."
    public const string FallbackColorHex = "#22d3ee";

    private volatile Dictionary<string, string> _colors = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task<string> GetColorAsync(string category, CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastRefreshUtc > RefreshInterval) await RefreshAsync(ct);
        return _colors.GetValueOrDefault(category, FallbackColorHex);
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        // Non-blocking: if another concurrent poll (a different camera's overlay session, or the
        // same one) is already refreshing, this one just uses whatever's currently cached rather
        // than queuing up behind the refresh — a box overlay tick that's briefly stale by one
        // refresh cycle (30s) is a non-issue for a feed that's already only updated 5-10 times a
        // second and re-derived from scratch on the very next poll either way.
        if (!await _refreshGate.WaitAsync(0, ct)) return;
        try
        {
            if (DateTime.UtcNow - _lastRefreshUtc <= RefreshInterval) return; // someone else just refreshed while we waited for the gate

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await db.DetectedObjectCategories.AsNoTracking()
                .Select(c => new { c.Name, c.ColorHex })
                .ToListAsync(ct);

            _colors = rows.ToDictionary(r => r.Name, r => r.ColorHex, StringComparer.OrdinalIgnoreCase);
            _lastRefreshUtc = DateTime.UtcNow;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
