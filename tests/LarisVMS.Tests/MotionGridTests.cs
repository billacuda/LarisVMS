using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>Covers MotionGrid — Grid mode's mask bitset and per-cell scoring, pass 3c-2 of the
/// detection/hardware-acceleration overhaul. No ffmpeg/session involved, same pure-logic-tested-
/// directly approach as MotionDetectorTests/ZoneRasterizerTests.</summary>
public class MotionGridTests
{
    [Theory]
    [InlineData(16, 32)]  // 256 bits -> 32 bytes
    [InlineData(32, 128)] // 1024 bits -> 128 bytes
    [InlineData(64, 512)] // 4096 bits -> 512 bytes, matches the plan's own "~684 chars" sizing note
    public void EmptyMaskHasTheExpectedByteCountAtEachSupportedSize(int gridSize, int expectedBytes)
    {
        var mask = MotionGrid.EmptyMask(gridSize);
        Assert.Equal(expectedBytes, Convert.FromBase64String(mask).Length);
    }

    [Fact]
    public void EmptyMaskHasNoCellsMasked()
    {
        var mask = MotionGrid.EmptyMask(32);
        Assert.False(MotionGrid.IsCellMasked(mask, 32, 0));
        Assert.False(MotionGrid.IsCellMasked(mask, 32, 1023));
    }

    [Fact]
    public void SetCellMaskedTogglesOnlyThatCell()
    {
        var mask = MotionGrid.EmptyMask(4); // 16 cells
        mask = MotionGrid.SetCellMasked(mask, 4, cellIndex: 5, masked: true);

        Assert.True(MotionGrid.IsCellMasked(mask, 4, 5));
        Assert.False(MotionGrid.IsCellMasked(mask, 4, 4));
        Assert.False(MotionGrid.IsCellMasked(mask, 4, 6));

        mask = MotionGrid.SetCellMasked(mask, 4, cellIndex: 5, masked: false);
        Assert.False(MotionGrid.IsCellMasked(mask, 4, 5));
    }

    [Fact]
    public void NullOrEmptyMaskTreatsEveryCellAsUnmasked()
    {
        Assert.False(MotionGrid.IsCellMasked(null, 32, 0));
        Assert.False(MotionGrid.IsCellMasked("", 32, 500));
    }

    [Fact]
    public void CorruptedMaskFailsOpenRatherThanThrowing()
    {
        Assert.False(MotionGrid.IsCellMasked("not valid base64!!", 32, 0));
    }

    [Fact]
    public void UndersizedMaskFromASmallerPriorGridSizeTreatsNewCellsAsUnmasked()
    {
        // Simulates a grid size increase with no re-save yet — the stored mask is shorter than the
        // new size needs.
        var smallMask = MotionGrid.EmptyMask(4);
        Assert.False(MotionGrid.IsCellMasked(smallMask, 64, 4000)); // well past the old mask's own length
    }

    [Fact]
    public void RasterizeExcludesOnlyPixelsInsideMaskedCells()
    {
        // 2x2 grid over a 4x4 frame — each cell is exactly a 2x2 pixel block. Mask cell 0 (top-left).
        var mask = MotionGrid.SetCellMasked(MotionGrid.EmptyMask(2), 2, cellIndex: 0, masked: true);
        var raster = MotionGrid.Rasterize(mask, 2, width: 4, height: 4);

        bool[] expected =
        [
            false, false, true, true,
            false, false, true, true,
            true,  true,  true, true,
            true,  true,  true, true,
        ];
        Assert.Equal(expected, raster);
    }

    [Fact]
    public void RasterizeOfAnEmptyMaskWatchesTheWholeFrame()
    {
        var raster = MotionGrid.Rasterize(MotionGrid.EmptyMask(4), 4, width: 8, height: 6);
        Assert.All(raster, watched => Assert.True(watched));
    }

    [Fact]
    public void RasterizeHandlesAFrameThatDoesNotDivideEvenlyByGridSize()
    {
        // 5x5 frame, 2x2 grid — cells are non-square/uneven sizes, but every pixel must still land
        // in exactly one cell with no index ever going out of range.
        var mask = MotionGrid.SetCellMasked(MotionGrid.EmptyMask(2), 2, cellIndex: 3, masked: true); // bottom-right cell
        var raster = MotionGrid.Rasterize(mask, 2, width: 5, height: 5);

        Assert.Equal(25, raster.Length);
        Assert.False(raster[24]); // bottom-right-most pixel falls in the masked cell
        Assert.True(raster[0]);   // top-left-most pixel does not
    }

    [Fact]
    public void ScoreCellsWithASingleCellMatchesMotionDetectorScoreOverTheWholeFrame()
    {
        byte[] previous = [0, 0, 0, 0, 100, 100, 100, 100, 0, 0, 0, 0];
        byte[] current = [0, 0, 0, 0, 200, 200, 100, 100, 0, 0, 0, 0]; // 2 of 12 pixels changed
        var allTrue = new bool[12];
        Array.Fill(allTrue, true);

        var expected = MotionDetector.Score(previous, current, allTrue, pixelDeltaThreshold: 25);
        var cellScores = MotionGrid.ScoreCells(previous, current, gridSize: 1, width: 4, height: 3, pixelDeltaThreshold: 25);

        Assert.Single(cellScores);
        Assert.Equal(expected, cellScores[0], precision: 10);
    }

    [Fact]
    public void ScoreCellsIsolatesChangeToItsOwnCell()
    {
        // 2x2 grid over a 4x4 frame. Only the top-left 2x2 block (cell 0) changes.
        var previous = new byte[16];
        var current = new byte[16];
        current[0] = 200; current[1] = 200; current[4] = 200; current[5] = 200; // cell 0's four pixels

        var scores = MotionGrid.ScoreCells(previous, current, gridSize: 2, width: 4, height: 4, pixelDeltaThreshold: 25);

        Assert.Equal(1.0, scores[0], precision: 10); // cell 0: all 4 pixels changed
        Assert.Equal(0.0, scores[1], precision: 10);
        Assert.Equal(0.0, scores[2], precision: 10);
        Assert.Equal(0.0, scores[3], precision: 10);
    }

    [Fact]
    public void HasAnyUnmaskedCellIsTrueForAnEmptyMask()
    {
        Assert.True(MotionGrid.HasAnyUnmaskedCell(MotionGrid.EmptyMask(4), 4));
        Assert.True(MotionGrid.HasAnyUnmaskedCell(null, 4));
    }

    [Fact]
    public void HasAnyUnmaskedCellIsFalseWhenEveryCellIsMasked()
    {
        var mask = MotionGrid.EmptyMask(2); // 4 cells
        for (var i = 0; i < 4; i++) mask = MotionGrid.SetCellMasked(mask, 2, i, masked: true);

        Assert.False(MotionGrid.HasAnyUnmaskedCell(mask, 2));
    }

    [Fact]
    public void HasAnyUnmaskedCellIsTrueWhenOnlySomeCellsAreMasked()
    {
        var mask = MotionGrid.SetCellMasked(MotionGrid.EmptyMask(2), 2, cellIndex: 0, masked: true);
        Assert.True(MotionGrid.HasAnyUnmaskedCell(mask, 2));
    }

    [Fact]
    public void ScoreCellsThrowsOnMismatchedFrameLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            MotionGrid.ScoreCells(new byte[16], new byte[15], gridSize: 2, width: 4, height: 4, pixelDeltaThreshold: 25));
    }
}
