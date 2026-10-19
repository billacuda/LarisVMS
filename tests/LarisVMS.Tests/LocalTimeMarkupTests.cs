using LarisVMS.Web.Helpers;

namespace LarisVMS.Tests;

/// <summary>ToUtcIso feeds local-time.js the instant to show in the viewer's zone, so it must always
/// carry a "Z" and treat a database value (Kind Unspecified, stored as UTC) as UTC.</summary>
public class LocalTimeMarkupTests
{
    [Fact]
    public void UnspecifiedIsTreatedAsUtc()
        => Assert.Equal("2026-10-13T15:04:05Z", new DateTime(2026, 10, 13, 15, 4, 5, DateTimeKind.Unspecified).ToUtcIso());

    [Fact]
    public void UtcIsKept()
        => Assert.Equal("2026-10-13T15:04:05Z", new DateTime(2026, 10, 13, 15, 4, 5, DateTimeKind.Utc).ToUtcIso());

    [Fact]
    public void LocalIsConverted()
    {
        var utc = new DateTime(2026, 10, 13, 15, 4, 5, DateTimeKind.Utc);
        Assert.Equal("2026-10-13T15:04:05Z", utc.ToLocalTime().ToUtcIso());
    }
}
