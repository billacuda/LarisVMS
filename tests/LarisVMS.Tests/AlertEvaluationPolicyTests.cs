using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class AlertEvaluationPolicyTests
{
    [Fact]
    public void ACameraThatHasNeverReportedIsNotReporting()
    {
        Assert.True(AlertEvaluationPolicy.IsCameraNotReporting(null, DateTime.UtcNow));
    }

    [Fact]
    public void ACameraReportedWithinTheFreshnessWindowIsReporting()
    {
        var now = DateTime.UtcNow;
        Assert.False(AlertEvaluationPolicy.IsCameraNotReporting(now.AddSeconds(-10), now));
    }

    [Fact]
    public void ACameraReportedPastTheFreshnessWindowIsNotReporting()
    {
        var now = DateTime.UtcNow;
        Assert.True(AlertEvaluationPolicy.IsCameraNotReporting(now.AddMinutes(-5), now));
    }

    [Fact]
    public void ANodeThatHasNeverBeenSeenIsOffline()
    {
        Assert.True(AlertEvaluationPolicy.IsNodeOffline(null, DateTime.UtcNow));
    }

    [Fact]
    public void ANodeSeenWithinTheOnlineWindowIsOnline()
    {
        var now = DateTime.UtcNow;
        Assert.False(AlertEvaluationPolicy.IsNodeOffline(now.AddSeconds(-30), now));
    }

    [Fact]
    public void ANodeSeenPastTheOnlineWindowIsOffline()
    {
        var now = DateTime.UtcNow;
        Assert.True(AlertEvaluationPolicy.IsNodeOffline(now.AddMinutes(-10), now));
    }

    [Fact]
    public void UnknownStorageStatsNeverCountAsLow()
    {
        Assert.False(AlertEvaluationPolicy.IsNodeStorageLow(null, null, 10));
        Assert.False(AlertEvaluationPolicy.IsNodeStorageLow(100, null, 10));
        Assert.False(AlertEvaluationPolicy.IsNodeStorageLow(null, 100, 10));
        Assert.False(AlertEvaluationPolicy.IsNodeStorageLow(100, 0, 10));
    }

    [Fact]
    public void FreeSpaceBelowTheThresholdIsLow()
    {
        Assert.True(AlertEvaluationPolicy.IsNodeStorageLow(freeBytes: 5, totalBytes: 100, thresholdPercent: 10));
    }

    [Fact]
    public void FreeSpaceAtOrAboveTheThresholdIsNotLow()
    {
        Assert.False(AlertEvaluationPolicy.IsNodeStorageLow(freeBytes: 10, totalBytes: 100, thresholdPercent: 10));
        Assert.False(AlertEvaluationPolicy.IsNodeStorageLow(freeBytes: 50, totalBytes: 100, thresholdPercent: 10));
    }

    [Fact]
    public void ARuleThatsNeverFiredShouldFire()
    {
        Assert.True(AlertEvaluationPolicy.ShouldFire(null, DateTime.UtcNow, 60));
    }

    [Fact]
    public void ARuleStillWithinItsCooldownShouldNotFire()
    {
        var now = DateTime.UtcNow;
        Assert.False(AlertEvaluationPolicy.ShouldFire(now.AddMinutes(-30), now, 60));
    }

    [Fact]
    public void ARulePastItsCooldownShouldFireAgain()
    {
        var now = DateTime.UtcNow;
        Assert.True(AlertEvaluationPolicy.ShouldFire(now.AddMinutes(-61), now, 60));
    }
}
