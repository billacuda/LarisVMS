using LarisVMS.Infrastructure.Services;
using Row = LarisVMS.Infrastructure.Services.SnapshotObjectCollapser.Row;

namespace LarisVMS.Tests;

/// <summary>Snapshot badges: track-ID fragments of one walk-through fold into one object, while
/// objects in frame at the same time stay separate.</summary>
public class SnapshotObjectCollapserTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 17, 14, 31, DateTimeKind.Utc);

    private static Row Person(double from, double to, string? upper, string? lower, double confidence, double? speed = null) =>
        new("person", upper, upper, lower, speed, confidence, T0.AddSeconds(from), T0.AddSeconds(to));

    [Fact]
    public void SequentialFragmentsOfOnePersonFoldIntoOne()
    {
        var rows = Enumerable.Range(0, 6).Select(i => Person(i * 2, i * 2 + 1.8, "black", "black", 0.5 + i * 0.01));

        var objects = SnapshotObjectCollapser.Collapse(rows);

        var only = Assert.Single(objects);
        Assert.Equal("black", only.Object.UpperColor);
        Assert.Equal("black", only.Object.LowerColor);
    }

    [Fact]
    public void HighestConfidenceColorWinsPerPart()
    {
        var objects = SnapshotObjectCollapser.Collapse([
            Person(0, 2, "black", "black", 0.40),
            Person(2.2, 4, "black", "gray", 0.90),
            Person(4.1, 6, "gray", "black", 0.55),
        ]);

        var only = Assert.Single(objects);
        Assert.Equal("black", only.Object.UpperColor);
        Assert.Equal("gray", only.Object.LowerColor);
        Assert.Equal(0.90, only.Confidence);
    }

    [Fact]
    public void TwoPeopleInFrameTogetherStayTwoEvenWhenFragmented()
    {
        var objects = SnapshotObjectCollapser.Collapse([
            Person(0, 3, "black", "gray", 0.80),
            Person(0.5, 3.5, "black", "blue", 0.85),
            Person(3.2, 6, "black", "gray", 0.60),
            Person(3.6, 7, "black", "blue", 0.70),
        ]);

        Assert.Equal(2, objects.Count);
        Assert.Equal(["blue", "gray"], objects.Select(o => o.Object.LowerColor!).Order().ToArray());
        Assert.All(objects, o => Assert.Equal("black", o.Object.UpperColor));
    }

    [Fact]
    public void FragmentContinuesTheSlotWhoseColorsItMatches()
    {
        // Both people leave before the next fragment starts, so either slot is free. The colors pick.
        var objects = SnapshotObjectCollapser.Collapse([
            Person(0, 2, "red", "blue", 0.80),
            Person(0, 2.5, "white", "black", 0.80),
            Person(3, 5, "red", "blue", 0.60),
        ]);

        Assert.Equal(2, objects.Count);
        Assert.Contains(objects, o => o.Object.UpperColor == "red" && o.Object.LowerColor == "blue");
        Assert.Contains(objects, o => o.Object.UpperColor == "white" && o.Object.LowerColor == "black");
    }

    [Fact]
    public void SpeedReadingSurvivesTheFold()
    {
        var objects = SnapshotObjectCollapser.Collapse([
            Person(0, 2, "black", "gray", 0.80),
            Person(2.1, 4, null, null, 0.50, speed: 7.5),
        ]);

        var only = Assert.Single(objects);
        Assert.Equal(7.5, only.Object.SpeedKph);
        Assert.Equal("black", only.Object.UpperColor);
    }

    [Fact]
    public void DifferentLabelsNeverFoldTogether()
    {
        var objects = SnapshotObjectCollapser.Collapse([
            Person(0, 2, "black", "gray", 0.80),
            new Row("car", "silver", null, null, null, 0.9, T0.AddSeconds(3), T0.AddSeconds(5)),
        ]);

        Assert.Equal(2, objects.Count);
    }
}
