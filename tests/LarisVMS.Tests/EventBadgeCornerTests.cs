using LarisVMS.Core;

namespace LarisVMS.Tests;

/// <summary>
/// The badge-corner allowlist. This value reaches the browser and drives positioning classes, so
/// "anything not on the list becomes the default" is a security property, not just tidiness — the
/// same reasoning Branding applies to values it interpolates into CSS.
/// </summary>
public class EventBadgeCornerTests
{
    [Theory]
    [InlineData(EventBadgeCorner.TopLeft)]
    [InlineData(EventBadgeCorner.TopRight)]
    [InlineData(EventBadgeCorner.BottomRight)]
    [InlineData(EventBadgeCorner.BottomLeft)]
    public void KeepsEveryAllowedCorner(string corner)
    {
        Assert.Equal(corner, EventBadgeCorner.Normalize(corner));
    }

    [Fact]
    public void IsCaseInsensitiveButReturnsTheCanonicalCasing()
    {
        Assert.Equal(EventBadgeCorner.BottomRight, EventBadgeCorner.Normalize("bottomright"));
        Assert.Equal(EventBadgeCorner.TopRight, EventBadgeCorner.Normalize("TOPRIGHT"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Middle")]
    [InlineData("top-0 start-0")]
    [InlineData("\"><script>alert(1)</script>")]
    public void FallsBackToTheDefaultForAnythingElse(string? stored)
    {
        Assert.Equal(EventBadgeCorner.Default, EventBadgeCorner.Normalize(stored));
    }

    [Fact]
    public void EveryAllowedCornerHasItsOwnLabel()
    {
        var labels = EventBadgeCorner.All.Select(EventBadgeCorner.Label).ToList();
        Assert.Equal(EventBadgeCorner.All.Length, labels.Distinct().Count());
        Assert.All(labels, l => Assert.False(string.IsNullOrWhiteSpace(l)));
    }
}
