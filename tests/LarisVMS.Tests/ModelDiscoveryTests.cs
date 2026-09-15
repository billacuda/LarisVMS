using LarisVMS.Vision.Models;

namespace LarisVMS.Tests;

/// <summary>
/// Covers <see cref="ModelDiscovery.ParseUltralyticsNames"/> — the parser for Ultralytics' own
/// embedded <c>names</c> metadata (a <c>str()</c> of a Python dict, not JSON: single-quoted values,
/// unquoted integer keys). This is real-world exporter output LarisVMS.Vision.Models.ModelDiscovery
/// now recognizes so a standard YOLOv8/11/26 export resolves without a hand-written sidecar (see the
/// LarisVMS+SideGlance planning notes, Section B2, for why the previous bespoke-key-only convention
/// left every real-world export unresolved).
/// </summary>
public class ModelDiscoveryTests
{
    [Fact]
    public void ParsesAStandardCocoStyleNamesDict()
    {
        var labels = ModelDiscovery.ParseUltralyticsNames("{0: 'person', 1: 'bicycle', 2: 'car'}");

        Assert.NotNull(labels);
        Assert.Equal(["person", "bicycle", "car"], labels);
    }

    [Fact]
    public void OrdersByIntegerKeyRegardlessOfDictOrder()
    {
        var labels = ModelDiscovery.ParseUltralyticsNames("{2: 'car', 0: 'person', 1: 'bicycle'}");

        Assert.Equal(["person", "bicycle", "car"], labels);
    }

    [Fact]
    public void HandlesASingleEntry()
    {
        var labels = ModelDiscovery.ParseUltralyticsNames("{0: 'widget'}");

        Assert.Equal(["widget"], labels);
    }

    [Fact]
    public void HandlesDoubleQuotedValues()
    {
        var labels = ModelDiscovery.ParseUltralyticsNames("{0: \"person\", 1: \"bicycle\"}");

        Assert.Equal(["person", "bicycle"], labels);
    }

    [Fact]
    public void ALabelContainingACommaDoesNotBreakSplitting()
    {
        // Splitting must respect the quotes around each value — a naive comma-split would cut this
        // label in half.
        var labels = ModelDiscovery.ParseUltralyticsNames("{0: 'widget, deluxe', 1: 'gadget'}");

        Assert.Equal(["widget, deluxe", "gadget"], labels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not a dict at all")]
    [InlineData("[0, 1, 2]")]
    [InlineData("{0 person, 1: 'bicycle'}")] // missing colon on the first entry
    [InlineData("{0: person, 1: 'bicycle'}")] // unquoted value
    [InlineData("{a: 'person'}")] // non-integer key
    public void ReturnsNullForAnythingThatDoesNotMatchTheExpectedShape(string raw)
    {
        Assert.Null(ModelDiscovery.ParseUltralyticsNames(raw));
    }
}
