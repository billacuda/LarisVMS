using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The application-log retention decision. 0 meaning "keep forever" is the case worth pinning down:
/// read the other way it would delete every log file on the next sweep, which is exactly the kind of
/// setting an admin types 0 into expecting "unlimited".
/// </summary>
public class LogsRetentionTests
{
    private static readonly DateTime Now = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void DeletesAFileOlderThanTheWindow()
    {
        Assert.True(LogsRetentionService.ShouldDelete(Now.AddDays(-15), Now, 14));
    }

    [Fact]
    public void KeepsAFileInsideTheWindow()
    {
        Assert.False(LogsRetentionService.ShouldDelete(Now.AddDays(-13), Now, 14));
    }

    [Fact]
    public void KeepsAFileExactlyAtTheWindowEdge()
    {
        // Strictly-greater-than, so a file that is exactly `retentionDays` old survives one more
        // cycle rather than being deleted the instant it reaches the boundary.
        Assert.False(LogsRetentionService.ShouldDelete(Now.AddDays(-14), Now, 14));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void KeepsEverythingWhenRetentionIsNotPositive(int retentionDays)
    {
        Assert.False(LogsRetentionService.ShouldDelete(Now.AddYears(-5), Now, retentionDays));
    }

    [Fact]
    public void RespectsAShortenedWindow()
    {
        Assert.True(LogsRetentionService.ShouldDelete(Now.AddDays(-2), Now, 1));
        Assert.False(LogsRetentionService.ShouldDelete(Now.AddHours(-2), Now, 1));
    }
}
