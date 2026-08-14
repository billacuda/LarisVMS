using LarisVMS.Media;

namespace LarisVMS.Tests;

public class ZoneRasterizerTests
{
    [Fact]
    public void ParsesPointsArray()
    {
        var json = """{"points":[[0.1,0.1],[0.9,0.1],[0.9,0.9],[0.1,0.9]]}""";

        var points = ZoneRasterizer.ParsePolygon(json);

        Assert.Equal(4, points.Count);
        Assert.Equal(0.1, points[0].X);
        Assert.Equal(0.1, points[0].Y);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"points":"not an array"}""")]
    [InlineData("""{"points":[[0.1]]}""")] // single-element inner array, not a valid [x,y] pair
    public void MalformedInputReturnsEmptyRatherThanThrowing(string json)
    {
        var points = ZoneRasterizer.ParsePolygon(json);

        Assert.Empty(points);
    }

    [Fact]
    public void RasterizeFullFrameRectangleMasksEveryPixel()
    {
        var polygon = new[]
        {
            new ZoneRasterizer.Point(0.0, 0.0),
            new ZoneRasterizer.Point(1.0, 0.0),
            new ZoneRasterizer.Point(1.0, 1.0),
            new ZoneRasterizer.Point(0.0, 1.0)
        };

        var mask = ZoneRasterizer.Rasterize(polygon, width: 10, height: 10);

        Assert.All(mask, m => Assert.True(m));
    }

    [Fact]
    public void RasterizeLeftHalfOnlyMasksLeftPixels()
    {
        var polygon = new[]
        {
            new ZoneRasterizer.Point(0.0, 0.0),
            new ZoneRasterizer.Point(0.5, 0.0),
            new ZoneRasterizer.Point(0.5, 1.0),
            new ZoneRasterizer.Point(0.0, 1.0)
        };

        var mask = ZoneRasterizer.Rasterize(polygon, width: 10, height: 10);

        for (var y = 0; y < 10; y++)
        {
            for (var x = 0; x < 10; x++)
            {
                var expected = x < 5; // pixel centers 0.05..0.45 fall inside [0,0.5); 0.55+ do not
                Assert.Equal(expected, mask[y * 10 + x]);
            }
        }
    }

    [Fact]
    public void FewerThanThreeVerticesRasterizesToAllFalse()
    {
        var polygon = new[] { new ZoneRasterizer.Point(0.5, 0.5), new ZoneRasterizer.Point(0.6, 0.6) };

        var mask = ZoneRasterizer.Rasterize(polygon, width: 4, height: 4);

        Assert.All(mask, Assert.False);
    }

    [Fact]
    public void SubtractRemovesExcludedPixels()
    {
        var mask = new[] { true, true, true, true };
        var exclude = new[] { true, false, true, false };

        var result = ZoneRasterizer.Subtract(mask, exclude);

        Assert.Equal([false, true, false, true], result);
    }

    [Fact]
    public void UnionCombinesBothMasks()
    {
        var a = new[] { true, false, false, false };
        var b = new[] { false, true, false, false };

        var result = ZoneRasterizer.Union(a, b);

        Assert.Equal([true, true, false, false], result);
    }
}
