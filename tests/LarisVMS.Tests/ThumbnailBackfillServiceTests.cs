using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>Exercises ThumbnailBackfillService's pure filesystem-facing selection logic against a
/// real temp directory tree — same pattern as StorageManagerTests, safe to run anywhere.</summary>
public class ThumbnailBackfillServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LarisVMSThumbnailBackfillTests_" + Guid.NewGuid());
    private readonly string _mainDir;
    private readonly string _thumbsDir;

    public ThumbnailBackfillServiceTests()
    {
        _mainDir = Path.Combine(_root, "main");
        _thumbsDir = Path.Combine(_root, "thumbs");
        Directory.CreateDirectory(_mainDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string WriteSegment(string relativePath, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_mainDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private void WriteThumbnail(string relativePathUnderThumbs)
    {
        var path = Path.Combine(_thumbsDir, relativePathUnderThumbs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [4, 5, 6]);
    }

    [Fact]
    public void FindsAFiveMinuteAlignedSegmentMissingItsThumbnail()
    {
        var old = DateTime.UtcNow.AddMinutes(-10);
        var segment = WriteSegment(@"2026\08\09\14\20260809T140500Z.mp4", old);

        var found = ThumbnailBackfillService.FindAlignedSegmentsMissingThumbnails(_mainDir, _thumbsDir, DateTime.UtcNow).ToList();

        Assert.Equal([segment], found);
    }

    [Theory]
    [InlineData("20260809T140100Z.mp4")] // minute not a multiple of 5
    [InlineData("20260809T140530Z.mp4")] // non-zero seconds
    public void SkipsSegmentsNotOnAFiveMinuteBoundary(string fileName)
    {
        WriteSegment(fileName, DateTime.UtcNow.AddMinutes(-10));

        var found = ThumbnailBackfillService.FindAlignedSegmentsMissingThumbnails(_mainDir, _thumbsDir, DateTime.UtcNow).ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void SkipsASegmentThatAlreadyHasItsThumbnailCached()
    {
        WriteSegment(@"2026\08\09\14\20260809T140500Z.mp4", DateTime.UtcNow.AddMinutes(-10));
        WriteThumbnail(@"2026\08\09\14\20260809T140500Z_o00.jpg");

        var found = ThumbnailBackfillService.FindAlignedSegmentsMissingThumbnails(_mainDir, _thumbsDir, DateTime.UtcNow).ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void SkipsASegmentTooFreshToTrustAsFullyWritten()
    {
        WriteSegment(@"2026\08\09\14\20260809T140500Z.mp4", DateTime.UtcNow.AddSeconds(-30));

        var found = ThumbnailBackfillService.FindAlignedSegmentsMissingThumbnails(_mainDir, _thumbsDir, DateTime.UtcNow).ToList();

        Assert.Empty(found);
    }

    [Fact]
    public void IgnoresFilesThatDoNotMatchTheSegmentFilenamePattern()
    {
        var stray = Path.Combine(_mainDir, "not-a-segment.mp4");
        File.WriteAllBytes(stray, [1]);
        File.SetLastWriteTimeUtc(stray, DateTime.UtcNow.AddMinutes(-10));

        var found = ThumbnailBackfillService.FindAlignedSegmentsMissingThumbnails(_mainDir, _thumbsDir, DateTime.UtcNow).ToList();

        Assert.Empty(found);
    }
}
