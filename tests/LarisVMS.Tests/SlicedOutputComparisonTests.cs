using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>
/// SlicedOutputComparison — the numeric check that decides whether a GPU-native batched Slice graph
/// can be trusted. Outputs here mimic an end-to-end detection head: rows of
/// (x1, y1, x2, y2, score, class) in input-pixel units, sorted by score.
/// </summary>
public class SlicedOutputComparisonTests
{
    private const int RowWidth = 6;

    // Three tiles with clearly different detections.
    private static float[][] Tiles() =>
    [
        [10, 20, 110, 220, 0.91f, 2, 300, 40, 380, 160, 0.62f, 0, 0, 0, 0, 0, 0, 0],
        [400, 300, 520, 420, 0.88f, 7, 50, 500, 140, 610, 0.55f, 2, 0, 0, 0, 0, 0, 0],
        [220, 100, 260, 190, 0.77f, 0, 600, 10, 639, 90, 0.41f, 15, 0, 0, 0, 0, 0, 0],
    ];

    private static float[][] Copy(float[][] slots) => slots.Select(s => (float[])s.Clone()).ToArray();

    [Fact]
    public void IdenticalOutputsAgree()
    {
        var tiles = Tiles();
        Assert.Null(SlicedOutputComparison.Compare("output0", Copy(tiles), tiles, RowWidth, out _));
    }

    [Fact]
    public void SubPixelNoiseIsAccepted()
    {
        // What two TensorRT engines and float-vs-byte preprocessing actually produce: box corners a
        // quarter pixel apart, scores a hair different.
        var tiles = Tiles();
        var batched = Copy(tiles);
        foreach (var slot in batched)
        {
            for (var i = 0; i < slot.Length; i += RowWidth)
            {
                if (slot[i + 4] == 0) continue;
                slot[i] += 0.25f; slot[i + 1] -= 0.25f; slot[i + 2] += 0.25f;
                slot[i + 4] -= 0.004f;
            }
        }

        Assert.Null(SlicedOutputComparison.Compare("output0", batched, tiles, RowWidth, out _));
    }

    [Fact]
    public void NearTiedRowsTradingPlacesAreAccepted()
    {
        var tiles = Tiles();
        var batched = Copy(tiles);
        // Swap slot 0's two detections, as score-sorted output does when their scores nearly tie.
        var first = batched[0][..RowWidth];
        Array.Copy(batched[0], RowWidth, batched[0], 0, RowWidth);
        Array.Copy(first, 0, batched[0], RowWidth, RowWidth);

        Assert.Null(SlicedOutputComparison.Compare("output0", batched, tiles, RowWidth, out _));
    }

    [Fact]
    public void EverySlotCarryingOneTilesContentIsRejected()
    {
        // The frozen batch index export bug: the batched graph returns tile 0's detections in every slot.
        var tiles = Tiles();
        var batched = new[] { (float[])tiles[0].Clone(), (float[])tiles[0].Clone(), (float[])tiles[0].Clone() };

        var reason = SlicedOutputComparison.Compare("output0", batched, tiles, RowWidth, out _);

        Assert.NotNull(reason);
        Assert.Contains("slot 1", reason);
    }

    [Fact]
    public void SlotsInTheWrongOrderAreRejected()
    {
        var tiles = Tiles();
        var batched = new[] { (float[])tiles[1].Clone(), (float[])tiles[0].Clone(), (float[])tiles[2].Clone() };

        Assert.NotNull(SlicedOutputComparison.Compare("output0", batched, tiles, RowWidth, out _));
    }

    [Fact]
    public void AllZeroOutputsAgreeAndSkipTheIdentityTest()
    {
        var zeros = new[] { new float[18], new float[18], new float[18] };

        Assert.Null(SlicedOutputComparison.Compare("output0", Copy(zeros), zeros, RowWidth, out var identitySkipped));
        Assert.True(identitySkipped);
    }

    [Fact]
    public void MissingSlotIsRejected()
    {
        var tiles = Tiles();
        Assert.NotNull(SlicedOutputComparison.Compare("output0", Copy(tiles)[..2], tiles, RowWidth, out _));
    }
}
