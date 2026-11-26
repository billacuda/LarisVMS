using LarisVMS.Core;

namespace LarisVMS.Tests;

public class ExportFileNameTests
{
    private static readonly Guid Id = Guid.Parse("0123456789abcdef0123456789abcdef");

    [Fact]
    public void TwoExportsOfTheSameFootageGetTwoFiles() =>
        Assert.NotEqual(ExportFileName.WithItemId("Door_a_b.mp4", Guid.NewGuid()), ExportFileName.WithItemId("Door_a_b.mp4", Guid.NewGuid()));

    [Fact]
    public void TheIdIsLeftOutOfTheNamePeopleSee()
    {
        var file = ExportFileName.WithItemId("Door_NVR1_20261124T120000_20261124T130000.mp4", Id);
        Assert.Equal("Door_NVR1_20261124T120000_20261124T130000_0123456789abcdef0123456789abcdef.mp4", file);
        Assert.Equal("Door_NVR1_20261124T120000_20261124T130000.mp4", ExportFileName.Friendly(@"D:\rec\exports\" + file));
        Assert.Equal("Door.h264.mp4", ExportFileName.Friendly($"/rec/exports/Door_{Id:N}.h264.mp4"));
    }

    [Theory]
    [InlineData(@"D:\rec\exports\Frontdoor_NVR1.mp4", "Frontdoor_NVR1.mp4")]
    [InlineData("/rec/exports/Frontdoor_NVR1.h264.mp4", "Frontdoor_NVR1.h264.mp4")]
    public void AnExportMadeBeforeNamesHadAnIdKeepsItsName(string path, string expected) =>
        Assert.Equal(expected, ExportFileName.Friendly(path));
}
