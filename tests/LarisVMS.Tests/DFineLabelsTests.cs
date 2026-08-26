using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>Pins the two label tables against what was actually verified in the D-FINE
/// integration's Phase 0 (pulled directly from each variant's own Hugging Face config.json — see
/// DFineLabels' own doc comment). A count or spelling drift here would silently mislabel every
/// detection of the affected class.</summary>
public class DFineLabelsTests
{
    [Fact]
    public void Obj2CocoHasExactlyEightyClasses() => Assert.Equal(80, DFineLabels.Obj2Coco.Count);

    [Fact]
    public void Obj365HasExactlyThreeHundredSixtySixEntriesIncludingNone() => Assert.Equal(366, DFineLabels.Obj365.Count);

    [Fact]
    public void Obj365IndexZeroIsTheNonePaddingSlot() => Assert.Equal("None", DFineLabels.Obj365[0]);

    [Theory]
    [InlineData(0, "person")]
    [InlineData(3, "motorbike")]  // legacy spelling — not "motorcycle"
    [InlineData(4, "aeroplane")]  // legacy spelling — not "airplane"
    [InlineData(57, "sofa")]
    [InlineData(58, "pottedplant")]
    [InlineData(60, "diningtable")]
    [InlineData(62, "tvmonitor")]
    [InlineData(79, "toothbrush")]
    public void Obj2CocoIndicesMatchTheVerifiedModelConfig(int index, string expected) =>
        Assert.Equal(expected, DFineLabels.Obj2Coco[index]);

    [Theory]
    [InlineData(1, "Person")]
    [InlineData(57, "Wild Bird")]
    [InlineData(365, "Table Tennis")]
    public void Obj365IndicesMatchTheVerifiedModelConfig(int index, string expected) =>
        Assert.Equal(expected, DFineLabels.Obj365[index]);

    [Fact]
    public void NeitherTableHasDuplicateEntries()
    {
        Assert.Equal(DFineLabels.Obj2Coco.Count, DFineLabels.Obj2Coco.Distinct().Count());
        Assert.Equal(DFineLabels.Obj365.Count, DFineLabels.Obj365.Distinct().Count());
    }
}
