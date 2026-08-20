using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Web.Services;

/// <summary>
/// Pure decision logic behind AlertEvaluatorService — extracted so "is this condition true" and "has
/// the cooldown elapsed" are unit-testable without a real DbContext or clock. Reuses
/// DashboardService's own freshness/online windows so a camera/node the Dashboard calls "not
/// reporting"/"offline" is exactly the one an alert rule would fire on, never a second definition
/// silently drifting from the first.
/// </summary>
public static class AlertEvaluationPolicy
{
    public static bool IsCameraNotReporting(DateTime? healthReportedAtUtc, DateTime nowUtc) =>
        healthReportedAtUtc is null || nowUtc - healthReportedAtUtc.Value >= DashboardService.HealthFreshWindow;

    public static bool IsNodeOffline(DateTime? lastSeenAtUtc, DateTime nowUtc) =>
        lastSeenAtUtc is null || nowUtc - lastSeenAtUtc.Value >= DashboardService.NodeOnlineWindow;

    /// <summary>False (never fires) when the node hasn't reported storage stats at all — an unset
    /// pair means "unknown", not "zero free space".</summary>
    public static bool IsNodeStorageLow(long? freeBytes, long? totalBytes, int thresholdPercent)
    {
        if (freeBytes is null || totalBytes is not > 0) return false;
        var freePercent = freeBytes.Value * 100.0 / totalBytes.Value;
        return freePercent < thresholdPercent;
    }

    public static bool ShouldFire(DateTime? lastFiredAtUtc, DateTime nowUtc, int cooldownMinutes) =>
        lastFiredAtUtc is null || nowUtc - lastFiredAtUtc.Value >= TimeSpan.FromMinutes(cooldownMinutes);
}
