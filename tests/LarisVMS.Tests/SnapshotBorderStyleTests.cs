using LarisVMS.Web.Helpers;

namespace LarisVMS.Tests;

/// <summary>
/// Ordering and gradient rules for the Snapshots-page multi-badge card frame. The order is "sort
/// ascending by hue", which fixes which color anchors the upper-left corner; the gradient string is
/// what gets interpolated into the card's style attribute.
/// </summary>
public class SnapshotBorderStyleTests
{
    [Fact]
    public void OrangeSortsBeforeBlue()
    {
        var ordered = SnapshotBorderStyle.OrderColors(["#06b6d4", "#f97316"]);
        Assert.Equal(new[] { "#f97316", "#06b6d4" }, ordered);
    }

    [Fact]
    public void GreenSortsBeforePink()
    {
        var ordered = SnapshotBorderStyle.OrderColors(["#fb7185", "#a3e635"]);
        Assert.Equal(new[] { "#a3e635", "#fb7185" }, ordered);
    }

    [Fact]
    public void RedRotatesToTheFront()
    {
        var ordered = SnapshotBorderStyle.OrderColors(["#8b5cf6", "#a3e635", "#ef4444"]);
        Assert.Equal("#ef4444", ordered[0]);
    }

    [Fact]
    public void DuplicateColorsCollapseToOneEntry()
    {
        var ordered = SnapshotBorderStyle.OrderColors(["#f97316", "#F97316", "#f97316"]);
        Assert.Equal(new[] { "#f97316" }, ordered);
    }

    [Fact]
    public void ShorthandHexIsExpandedAndMatchesItsLongForm()
    {
        var ordered = SnapshotBorderStyle.OrderColors(["#abc", "#aabbcc"]);
        Assert.Equal(new[] { "#aabbcc" }, ordered);
    }

    [Fact]
    public void InvalidAndBlankValuesAreDropped()
    {
        var ordered = SnapshotBorderStyle.OrderColors([null, "", "  ", "not-a-color", "rgb(1,2,3)", "#f97316"]);
        Assert.Equal(new[] { "#f97316" }, ordered);
    }

    [Fact]
    public void GradientCssIsNullForZeroOrOneColor()
    {
        Assert.Null(SnapshotBorderStyle.GradientCss(SnapshotBorderStyle.OrderColors([])));
        Assert.Null(SnapshotBorderStyle.GradientCss(SnapshotBorderStyle.OrderColors(["#ef4444"])));
    }

    [Fact]
    public void TwoColorsProduceALinearGradientUpperLeftToLowerRight()
    {
        var css = SnapshotBorderStyle.GradientCss(["#f97316", "#06b6d4"]);
        Assert.Equal("linear-gradient(135deg, #f97316 0%, #06b6d4 100%)", css);
    }

    [Fact]
    public void ThreeColorsProduceAConicGradientAnchoredAtTheUpperLeft()
    {
        var css = SnapshotBorderStyle.GradientCss(["#ef4444", "#a3e635", "#06b6d4"]);
        Assert.Equal(
            "conic-gradient(from 315deg at 50% 50%, #ef4444 0deg, #a3e635 90deg, #06b6d4 225deg, #ef4444 360deg)",
            css);
    }

    [Fact]
    public void FourColorsPlaceOneColorPerCornerClockwise()
    {
        var css = SnapshotBorderStyle.GradientCss(["#ef4444", "#eab308", "#06b6d4", "#8b5cf6"]);
        Assert.Equal(
            "conic-gradient(from 315deg at 50% 50%, #ef4444 0deg, #eab308 90deg, #06b6d4 180deg, #8b5cf6 270deg, #ef4444 360deg)",
            css);
    }

    [Fact]
    public void FiveColorsSpreadEvenlyAndWrap()
    {
        var css = SnapshotBorderStyle.GradientCss(["#ef4444", "#f97316", "#eab308", "#a3e635", "#06b6d4"]);
        Assert.Equal(
            "conic-gradient(from 315deg at 50% 50%, #ef4444 0deg, #f97316 72deg, #eab308 144deg, #a3e635 216deg, #06b6d4 288deg, #ef4444 360deg)",
            css);
    }
}
