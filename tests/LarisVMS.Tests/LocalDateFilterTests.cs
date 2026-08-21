using LarisVMS.Web.Helpers;

namespace LarisVMS.Tests;

/// <summary>Covers the local-day → UTC-bounds conversion behind the Snapshots and Audit Logs date
/// filters. Deliberately written to be timezone-independent: every assertion converts back to local
/// and checks the *local* wall-clock, so these pass identically on a UTC build agent and on a UTC-7
/// deployment (which is where the original bug surfaced — a selected day stopped at 4:59 PM).</summary>
public class LocalDateFilterTests
{
    [Fact]
    public void StartOfDayIsLocalMidnightExpressedInUtc()
    {
        var utc = LocalDateFilter.StartOfDayUtc("2026-08-20");

        Assert.NotNull(utc);
        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);
        Assert.Equal(new DateTime(2026, 8, 20, 0, 0, 0), utc.Value.ToLocalTime());
    }

    [Fact]
    public void EndOfDayIsTheLastInstantOfTheLocalDayExpressedInUtc()
    {
        var utc = LocalDateFilter.EndOfDayUtc("2026-08-20");

        Assert.NotNull(utc);
        Assert.Equal(DateTimeKind.Utc, utc!.Value.Kind);

        var local = utc!.Value.ToLocalTime();
        Assert.Equal(new DateOnly(2026, 8, 20), DateOnly.FromDateTime(local));
        Assert.Equal(23, local.Hour);
        Assert.Equal(59, local.Minute);
    }

    [Fact]
    public void TheBoundsEncloseEveryInstantOfTheSelectedLocalDay()
    {
        // The actual regression: an instant late in the selected local day (7 PM, past the old
        // 23:59:59Z cutoff for any negative UTC offset) must fall inside the range.
        var from = LocalDateFilter.StartOfDayUtc("2026-08-20")!.Value;
        var to = LocalDateFilter.EndOfDayUtc("2026-08-20")!.Value;

        var lateInTheDay = new DateTime(2026, 8, 20, 19, 30, 0, DateTimeKind.Local).ToUniversalTime();
        var justAfterMidnight = new DateTime(2026, 8, 20, 0, 0, 30, DateTimeKind.Local).ToUniversalTime();

        Assert.InRange(lateInTheDay, from, to);
        Assert.InRange(justAfterMidnight, from, to);
    }

    [Fact]
    public void TheBoundsExcludeTheAdjacentDays()
    {
        var from = LocalDateFilter.StartOfDayUtc("2026-08-20")!.Value;
        var to = LocalDateFilter.EndOfDayUtc("2026-08-20")!.Value;

        var previousDay = new DateTime(2026, 8, 19, 23, 30, 0, DateTimeKind.Local).ToUniversalTime();
        var nextDay = new DateTime(2026, 8, 21, 0, 30, 0, DateTimeKind.Local).ToUniversalTime();

        Assert.True(previousDay < from);
        Assert.True(nextDay > to);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-date")]
    public void UnparseableOrEmptyInputMeansNoBound(string? input)
    {
        // An untouched filter field posts empty — callers rely on null meaning "don't narrow".
        Assert.Null(LocalDateFilter.StartOfDayUtc(input));
        Assert.Null(LocalDateFilter.EndOfDayUtc(input));
    }
}
