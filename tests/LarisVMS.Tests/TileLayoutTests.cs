using LarisVMS.Vision.Inference;
using TriggerPoint = LarisVMS.Vision.Inference.TileLayout.TriggerPoint;

namespace LarisVMS.Tests;

public class TileLayoutTests
{
    private const int FrameWidth = 3840;
    private const int FrameHeight = 2160;
    private const int TileSize = 640;

    [Fact]
    public void PlacesOneTileCenteredOnATriggerAwayFromAnyEdge()
    {
        // Center of the frame, normalized.
        var trigger = new TriggerPoint(0.5 - 0.01, 0.5 - 0.01, 0.02, 0.02);

        var tiles = TileLayout.PlaceTiles([trigger], TileSize, FrameWidth, FrameHeight);

        var tile = Assert.Single(tiles);
        Assert.Equal(TileSize, tile.Width);
        Assert.Equal(TileSize, tile.Height);
        // Trigger center is at (1920, 1080); tile should be centered there.
        Assert.Equal(1920 - (TileSize / 2), tile.X);
        Assert.Equal(1080 - (TileSize / 2), tile.Y);
    }

    [Fact]
    public void ATileNearTheTopLeftCornerClampsFullyInsideTheFrame()
    {
        // Centroid right at the frame's own top-left corner.
        var trigger = new TriggerPoint(0.0, 0.0, 0.01, 0.01);

        var tile = Assert.Single(TileLayout.PlaceTiles([trigger], TileSize, FrameWidth, FrameHeight));

        Assert.True(tile.X >= 0);
        Assert.True(tile.Y >= 0);
        Assert.True(tile.X + tile.Width <= FrameWidth);
        Assert.True(tile.Y + tile.Height <= FrameHeight);
    }

    [Fact]
    public void ATileNearTheBottomRightCornerClampsFullyInsideTheFrame()
    {
        var trigger = new TriggerPoint(0.99, 0.99, 0.01, 0.01);

        var tile = Assert.Single(TileLayout.PlaceTiles([trigger], TileSize, FrameWidth, FrameHeight));

        Assert.True(tile.X + tile.Width <= FrameWidth);
        Assert.True(tile.Y + tile.Height <= FrameHeight);
    }

    [Fact]
    public void TwoTriggerPointsWithinHalfATileMergeIntoOneTile()
    {
        // Both centroids land well within TileSize/2 (320px) of each other.
        var a = new TriggerPoint(0.5, 0.5, 0.02, 0.02);
        var b = new TriggerPoint(0.5 + (100.0 / FrameWidth), 0.5, 0.02, 0.02); // 100px away

        var tiles = TileLayout.PlaceTiles([a, b], TileSize, FrameWidth, FrameHeight);

        Assert.Single(tiles);
    }

    [Fact]
    public void TwoTriggerPointsFartherThanHalfATileApartProduceTwoTiles()
    {
        var a = new TriggerPoint(0.1, 0.5, 0.02, 0.02);
        var b = new TriggerPoint(0.9, 0.5, 0.02, 0.02);

        var tiles = TileLayout.PlaceTiles([a, b], TileSize, FrameWidth, FrameHeight);

        Assert.Equal(2, tiles.Count);
    }

    [Fact]
    public void ATriggerWhoseOwnBoxExceedsTheTileSizeEmitsNoNativeTile()
    {
        // A box wider than the tile itself — the whole-frame pass already catches an object this large.
        var largeObject = new TriggerPoint(0.4, 0.4, 700.0 / FrameWidth, 0.1);

        var tiles = TileLayout.PlaceTiles([largeObject], TileSize, FrameWidth, FrameHeight);

        Assert.Empty(tiles);
    }

    [Fact]
    public void TilesPerTriggerBatchAreCappedAtTheConfiguredMaximum()
    {
        var farApartTriggers = Enumerable.Range(0, 10)
            .Select(i => new TriggerPoint(i * 0.09, 0.5, 0.01, 0.01))
            .ToList();

        var tiles = TileLayout.PlaceTiles(farApartTriggers, TileSize, FrameWidth, FrameHeight, maxTiles: 4);

        Assert.Equal(4, tiles.Count);
    }

    [Fact]
    public void AFrameSmallerThanTheTileSizeProducesNoTiles()
    {
        // A tile clamped smaller than the model's fixed network input would break batch preprocessing's
        // shape validation — the whole-frame pass already covers a frame this small anyway.
        var trigger = new TriggerPoint(0.4, 0.4, 0.05, 0.05);

        var tiles = TileLayout.PlaceTiles([trigger], TileSize, frameWidth: 320, frameHeight: 240);

        Assert.Empty(tiles);
    }

    [Fact]
    public void MapTileBoxToFrameAddsTheTilesOwnOriginToALocalBox()
    {
        var tile = new TileLayout.Tile(X: 1000, Y: 500, Width: TileSize, Height: TileSize);

        var (x, y, width, height) = TileLayout.MapTileBoxToFrame(tile, boxXInTile: 50, boxYInTile: 60, boxWidth: 80, boxHeight: 90);

        Assert.Equal(1050, x);
        Assert.Equal(560, y);
        Assert.Equal(80, width);
        Assert.Equal(90, height);
    }
}
