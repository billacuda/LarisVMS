using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>Privacy masks: the drawn shape (not its bounding box) is blacked out, edges err towards
/// hiding, and the re-encode writes MP4 the tee'd recording can actually read.</summary>
public class PrivacyMaskShapeTests
{
    private static readonly List<ZoneRasterizer.Point> Triangle = [new(0.1, 0.1), new(0.6, 0.1), new(0.1, 0.7)];

    [Fact]
    public void TheShapeIsMaskedButTheRestOfItsBoundingBoxIsNot()
    {
        var grid = PrivacyMaskFilterBuilder.BuildMaskGrid([Triangle])!;

        Assert.True(PrivacyMaskFilterBuilder.IsMasked(grid, 0.2, 0.2));
        Assert.True(PrivacyMaskFilterBuilder.IsMasked(grid, 0.12, 0.65)); // near the bottom tip
        Assert.False(PrivacyMaskFilterBuilder.IsMasked(grid, 0.5, 0.6));   // inside the bbox, outside the triangle
        Assert.False(PrivacyMaskFilterBuilder.IsMasked(grid, 0.9, 0.9));
    }

    [Fact]
    public void EdgesAndTinyShapesAreStillCovered()
    {
        // A sliver thinner than a grid cell: centre sampling alone would miss it.
        var sliver = new List<ZoneRasterizer.Point> { new(0.3, 0.3), new(0.302, 0.3), new(0.302, 0.5), new(0.3, 0.5) };
        var grid = PrivacyMaskFilterBuilder.BuildMaskGrid([sliver])!;
        Assert.True(PrivacyMaskFilterBuilder.IsMasked(grid, 0.301, 0.4));
        // Grown a cell all round: just past the triangle's top edge is still masked.
        Assert.True(PrivacyMaskFilterBuilder.IsMasked(PrivacyMaskFilterBuilder.BuildMaskGrid([Triangle])!, 0.3, 0.095));
    }

    [Fact]
    public void FiltersCoverExactlyTheGridAndNothingMeansNoFilters()
    {
        var filters = PrivacyMaskFilterBuilder.BuildMaskFilters([Triangle]);
        Assert.NotEmpty(filters);
        Assert.All(filters, f => Assert.Matches(@"^drawbox=x=iw\*[\d.]+:y=ih\*[\d.]+:w=iw\*[\d.]+:h=ih\*[\d.]+:color=black:t=fill$", f));

        var rects = PrivacyMaskFilterBuilder.ToRectangles(PrivacyMaskFilterBuilder.BuildMaskGrid([Triangle])!);
        var covered = rects.Sum(r => r.W * r.H);
        Assert.Equal(PrivacyMaskFilterBuilder.BuildMaskGrid([Triangle])!.Count(c => c), covered);

        Assert.Empty(PrivacyMaskFilterBuilder.BuildMaskFilters([]));
        Assert.Null(PrivacyMaskFilterBuilder.BuildMaskGrid([[new(0.1, 0.1), new(0.2, 0.2)]]));
    }

    [Fact]
    public void MaskedRecordingAndSubStreamWriteTeeSafeMp4()
    {
        var filters = PrivacyMaskFilterBuilder.BuildMaskFilters([Triangle]);

        var main = RecordingSession.BuildCodecArgs(new RecordingSessionOptions("ffmpeg", "rtsp://x", "out", PrivacyMaskFilters: filters));
        var sub = SubLiveSession.BuildFfmpegArgs(new SubLiveSessionOptions("ffmpeg", "rtsp://x/sub", PrivacyMaskFilters: filters)).ToList();

        foreach (var args in new[] { main.ToList(), sub })
        {
            Assert.Equal("+global_header", args[args.IndexOf("-flags") + 1]);
            Assert.Equal("expr:gte(t,n_forced*2)", args[args.IndexOf("-force_key_frames") + 1]);
            Assert.Equal("libx264", args[args.IndexOf("-c:v") + 1]);
        }
        // Without masks the Sub stream stays a zero-cost copy.
        Assert.Contains("copy", SubLiveSession.BuildFfmpegArgs(new SubLiveSessionOptions("ffmpeg", "rtsp://x/sub")));
    }
}
