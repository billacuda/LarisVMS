using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The daily re-probe's scheduling decision. Worth pinning down because both failure modes are bad
/// in different ways: running twice re-probes the whole fleet an extra time, and never running means
/// the feature silently does nothing.
/// </summary>
public class CameraReprobeScheduleTests
{
    private static readonly TimeOnly ThreeAm = new(3, 0);
    private static DateOnly Today => new(2026, 8, 17);
    private static DateTime At(int hour, int minute) => new(2026, 8, 17, hour, minute, 0, DateTimeKind.Local);

    [Fact]
    public void RunsOnceTheScheduledTimeHasArrived()
    {
        Assert.True(CameraReprobeService.ShouldRun(null, At(3, 0), ThreeAm));
    }

    [Fact]
    public void DoesNotRunBeforeTheScheduledTime()
    {
        Assert.False(CameraReprobeService.ShouldRun(null, At(2, 59), ThreeAm));
    }

    [Fact]
    public void DoesNotRunTwiceOnTheSameDay()
    {
        Assert.False(CameraReprobeService.ShouldRun(Today, At(3, 1), ThreeAm));
        Assert.False(CameraReprobeService.ShouldRun(Today, At(23, 59), ThreeAm));
    }

    [Fact]
    public void RunsAgainTheNextDay()
    {
        var yesterday = Today.AddDays(-1);
        Assert.True(CameraReprobeService.ShouldRun(yesterday, At(3, 0), ThreeAm));
    }

    [Fact]
    public void CatchesUpAfterAMissedMinute()
    {
        // A tick that lands late (slow previous pass, the app starting at 03:04) still runs the
        // day's schedule rather than skipping it entirely until tomorrow.
        Assert.True(CameraReprobeService.ShouldRun(null, At(3, 47), ThreeAm));
    }

    [Theory]
    [InlineData("03:00", 3, 0)]
    [InlineData("23:30", 23, 30)]
    [InlineData("00:00", 0, 0)]
    public void ParsesAValidTime(string stored, int hour, int minute)
    {
        Assert.Equal(new TimeOnly(hour, minute), CameraReprobeService.ParseTimeOfDay(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a time")]
    [InlineData("25:00")]
    public void FallsBackToTheDefaultForAnUnparseableTime(string? stored)
    {
        Assert.Equal(TimeOnly.Parse(CameraReprobeService.DefaultAtLocalTime),
            CameraReprobeService.ParseTimeOfDay(stored));
    }
}
