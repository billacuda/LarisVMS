using LarisVMS.Web.Helpers;

namespace LarisVMS.Tests;

/// <summary>Covers the server-side reads of View.LayoutJson — a shape owned by the client-side
/// editor, so these tests pin the parsing against realistic payloads (including malformed ones,
/// which must degrade rather than throw: CameraIds backs an audit entry and must never be the thing
/// that breaks opening a view).</summary>
public class ViewLayoutTests
{
    private const string TwoCellLayout = """
        {"cells":[
            {"id":"cell-1","x":0,"y":0,"w":6,"h":4,"aspect":"16:9","cameraId":"11111111-1111-1111-1111-111111111111","hideOnPhone":false},
            {"id":"cell-2","x":6,"y":0,"w":6,"h":4,"aspect":"16:9","cameraId":"22222222-2222-2222-2222-222222222222","hideOnPhone":false}
        ],"mobileTwoColumn":false}
        """;

    [Fact]
    public void CountsCells()
    {
        Assert.Equal(2, ViewLayout.CountCells(TwoCellLayout));
    }

    [Fact]
    public void ReturnsEachCellsCameraIdInLayoutOrder()
    {
        var ids = ViewLayout.CameraIds(TwoCellLayout);

        Assert.Equal([
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222")
        ], ids);
    }

    [Fact]
    public void DeduplicatesACameraPlacedInMoreThanOneCell()
    {
        var layout = """
            {"cells":[
                {"id":"a","cameraId":"11111111-1111-1111-1111-111111111111"},
                {"id":"b","cameraId":"11111111-1111-1111-1111-111111111111"}
            ]}
            """;

        Assert.Single(ViewLayout.CameraIds(layout));
    }

    [Fact]
    public void SkipsCellsWithAMissingOrUnparseableCameraId()
    {
        var layout = """
            {"cells":[
                {"id":"a"},
                {"id":"b","cameraId":"not-a-guid"},
                {"id":"c","cameraId":null},
                {"id":"d","cameraId":"11111111-1111-1111-1111-111111111111"}
            ]}
            """;

        Assert.Equal([Guid.Parse("11111111-1111-1111-1111-111111111111")], ViewLayout.CameraIds(layout));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    [InlineData("{}")]
    [InlineData("""{"cells":"not an array"}""")]
    [InlineData("""{"cells":[]}""")]
    public void DegradesToEmptyRatherThanThrowing(string? layoutJson)
    {
        Assert.Empty(ViewLayout.CameraIds(layoutJson));
        Assert.Equal(0, ViewLayout.CountCells(layoutJson));
    }
}
