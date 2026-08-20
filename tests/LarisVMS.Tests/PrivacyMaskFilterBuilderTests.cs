using LarisVMS.Media;

namespace LarisVMS.Tests;

public class PrivacyMaskFilterBuilderTests
{
    private static ZoneRasterizer.Point P(double x, double y) => new(x, y);

    [Fact]
    public void ARectanglePolygonProducesItsOwnBoundingBoxAsTheDrawbox()
    {
        var polygon = new[] { P(0.1, 0.2), P(0.4, 0.2), P(0.4, 0.5), P(0.1, 0.5) };

        var filters = PrivacyMaskFilterBuilder.BuildDrawboxFilters([polygon]);

        Assert.Equal(["drawbox=x=iw*0.1:y=ih*0.2:w=iw*0.3:h=ih*0.3:color=black:t=fill"], filters);
    }

    [Fact]
    public void ANonRectangularPolygonUsesItsAxisAlignedBoundingBox()
    {
        // A triangle spanning x in [0.2, 0.6], y in [0.1, 0.4].
        var polygon = new[] { P(0.4, 0.1), P(0.6, 0.4), P(0.2, 0.4) };

        var filters = PrivacyMaskFilterBuilder.BuildDrawboxFilters([polygon]);

        Assert.Equal(["drawbox=x=iw*0.2:y=ih*0.1:w=iw*0.4:h=ih*0.3:color=black:t=fill"], filters);
    }

    [Fact]
    public void OneFilterPerPolygonInGivenOrder()
    {
        var a = new[] { P(0.0, 0.0), P(0.1, 0.0), P(0.1, 0.1) };
        var b = new[] { P(0.5, 0.5), P(0.6, 0.5), P(0.6, 0.6) };

        var filters = PrivacyMaskFilterBuilder.BuildDrawboxFilters([a, b]);

        Assert.Equal(2, filters.Count);
        Assert.Contains("x=iw*0", filters[0]);
        Assert.Contains("x=iw*0.5", filters[1]);
    }

    [Fact]
    public void APolygonWithFewerThanThreePointsIsSkipped()
    {
        var line = new[] { P(0.1, 0.1), P(0.2, 0.2) };

        Assert.Empty(PrivacyMaskFilterBuilder.BuildDrawboxFilters([line]));
    }

    [Fact]
    public void CoordinatesOutsideZeroToOneAreClampedRatherThanProducingAnInvalidBox()
    {
        var polygon = new[] { P(-0.2, -0.1), P(1.3, -0.1), P(1.3, 1.2), P(-0.2, 1.2) };

        var filters = PrivacyMaskFilterBuilder.BuildDrawboxFilters([polygon]);

        Assert.Equal(["drawbox=x=iw*0:y=ih*0:w=iw*1:h=ih*1:color=black:t=fill"], filters);
    }

    [Fact]
    public void NoPolygonsProducesNoFilters()
    {
        Assert.Empty(PrivacyMaskFilterBuilder.BuildDrawboxFilters([]));
    }
}
