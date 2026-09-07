using Microsoft.Extensions.Logging.Abstractions;
using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// Archive-storage helpers (phase 2): the target-path mapping, the "is this under the root" check
/// the archive-outside-storage guard uses, the known-paths partition ReconcileAsync gates on, and
/// the atomic move itself — all exercised against a real temp directory tree, same as
/// StorageManagerTests.
/// </summary>
public class StorageManagerArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LarisVMSArchiveTests_" + Guid.NewGuid());

    public StorageManagerArchiveTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void BuildArchiveTargetPath_PreservesTheNestedDateHourSubpath()
    {
        var primaryMain = @"E:\rec\cam-abc\main";
        var archiveMain = @"D:\archive\cam-abc\main";
        var source = @"E:\rec\cam-abc\main\2026\09\06\14\20260906T143000Z.mp4";

        var target = StorageManager.BuildArchiveTargetPath(primaryMain, archiveMain, source);

        Assert.Equal(@"D:\archive\cam-abc\main\2026\09\06\14\20260906T143000Z.mp4", target);
    }

    [Theory]
    [InlineData(@"E:\rec", @"E:\rec", true)]
    [InlineData(@"E:\rec\cam-1", @"E:\rec", true)]
    [InlineData(@"E:\rec2", @"E:\rec", false)]
    [InlineData(@"D:\archive", @"E:\rec", false)]
    [InlineData("", @"E:\rec", false)]
    public void IsUnderStorageRoot(string candidate, string root, bool expected)
        => Assert.Equal(expected, StorageManager.IsUnderStorageRoot(candidate, root));

    [Fact]
    public void PartitionByRoot_SplitsPrimaryArchiveAndNeither()
    {
        var (primary, archive, other) = StorageManager.PartitionByRoot(
            new[]
            {
                @"E:\rec\cam-1\main\a.mp4",
                @"D:\archive\cam-1\main\b.mp4",
                @"F:\somewhere\else\c.mp4",
            },
            @"E:\rec", @"D:\archive");

        Assert.Equal(new[] { @"E:\rec\cam-1\main\a.mp4" }, primary);
        Assert.Equal(new[] { @"D:\archive\cam-1\main\b.mp4" }, archive);
        Assert.Equal(new[] { @"F:\somewhere\else\c.mp4" }, other);
    }

    [Fact]
    public void TryArchiveFile_MovesTheFileAndKeepsTheSourceUntilTheCallerRemovesIt()
    {
        var source = Path.Combine(_root, "src", "seg.mp4");
        var target = Path.Combine(_root, "arc", "seg.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, new byte[1234]);
        var mtime = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, mtime);

        var ok = StorageManager.TryArchiveFile(source, target, NullLogger.Instance);

        Assert.True(ok);
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(source)); // caller deletes it only after the web records the new path
        Assert.Equal(1234, new FileInfo(target).Length);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(target)); // age anchored to record time for the expiry pass
        Assert.False(File.Exists(target + ".tmp"));
    }

    [Fact]
    public void TryArchiveFile_IsIdempotentWhenTheTargetAlreadyExistsWithMatchingSize()
    {
        var source = Path.Combine(_root, "src", "seg.mp4");
        var target = Path.Combine(_root, "arc", "seg.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(source, new byte[500]);
        File.WriteAllBytes(target, new byte[500]);

        Assert.True(StorageManager.TryArchiveFile(source, target, NullLogger.Instance));
    }

    [Fact]
    public void TryArchiveFile_ReturnsFalseAndLeavesTheSourceWhenTheSourceIsMissing()
    {
        var ok = StorageManager.TryArchiveFile(
            Path.Combine(_root, "nope.mp4"), Path.Combine(_root, "arc", "nope.mp4"), NullLogger.Instance);
        Assert.False(ok);
    }

    [Fact]
    public void SelectImportableSegments_SkipsAPrimaryFileWhoseRowHasAlreadyMovedToTheArchivePath()
    {
        var cam = Guid.NewGuid();
        var primaryMain = Path.Combine(_root, $"cam-{cam}", "main");
        var archiveMain = Path.Combine(_root, "archive", $"cam-{cam}", "main");
        Directory.CreateDirectory(primaryMain);
        var stale = Path.Combine(primaryMain, "2026", "09", "seg.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllBytes(stale, new byte[100]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        File.SetCreationTimeUtc(stale, DateTime.UtcNow.AddHours(-3));

        // The web knows this segment at its ARCHIVE path — the source-delete after a landed relocate
        // report didn't happen (a crash). It must not be re-imported as a new segment.
        var known = new HashSet<string>(
            new[] { Path.Combine(archiveMain, "2026", "09", "seg.mp4") }, StringComparer.OrdinalIgnoreCase);

        var result = StorageManager.SelectImportableSegments(cam, new[] { new FileInfo(stale) }, known, primaryMain, archiveMain);

        Assert.Empty(result);
    }
}
