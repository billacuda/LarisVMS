using NidusVMS.Node;

namespace NidusVMS.Tests;

/// <summary>
/// Exercises StorageManager's eviction-decision helpers against a real temp directory tree — safe
/// to run anywhere (never touches a live node's actual recordings) while still proving the
/// filesystem-facing pieces (age filtering, oldest-first quota selection, bottom-up empty-directory
/// pruning) work against a real filesystem rather than a mocked one.
/// </summary>
public class StorageManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NidusVMSStorageManagerTests_" + Guid.NewGuid());

    public StorageManagerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string WriteFile(string relativePath, int bytes, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void EnumerateEvictableExcludesFilesYoungerThanTheSafetyMargin()
    {
        var now = DateTime.UtcNow;
        WriteFile("fresh.mp4", 100, now.AddMinutes(-1));
        WriteFile("old.mp4", 100, now.AddMinutes(-10));

        var files = StorageManager.EnumerateEvictable(_root, now);

        Assert.Single(files);
        Assert.Equal("old.mp4", Path.GetFileName(files[0].FullName));
    }

    [Fact]
    public void SelectRetentionEvictionsUsesAgeCutoffAndTreatsZeroAsKeepForever()
    {
        var now = DateTime.UtcNow;
        var files = new List<FileInfo>
        {
            new(WriteFile("2026/08/01/00/a.mp4", 10, now.AddDays(-10))),
            new(WriteFile("2026/08/07/00/b.mp4", 10, now.AddDays(-1))),
        };

        var evicted = StorageManager.SelectRetentionEvictions(files, now, retentionDays: 5);
        Assert.Single(evicted);
        Assert.Equal("a.mp4", Path.GetFileName(evicted[0].FullName));

        Assert.Empty(StorageManager.SelectRetentionEvictions(files, now, retentionDays: 0));
        Assert.Empty(StorageManager.SelectRetentionEvictions(files, now, retentionDays: null));
    }

    [Fact]
    public void SelectQuotaEvictionsDeletesOldestFirstUntilUnderCap()
    {
        var now = DateTime.UtcNow;
        var files = new List<FileInfo>
        {
            new(WriteFile("a.mp4", 100, now.AddHours(-3))),
            new(WriteFile("b.mp4", 100, now.AddHours(-2))),
            new(WriteFile("c.mp4", 100, now.AddHours(-1))),
        };

        // 300 bytes total, quota 150 — must evict the two oldest (a, b) to get to 100, which is
        // under the 150 cap; it should not also evict c.
        var evicted = StorageManager.SelectQuotaEvictions(files, quotaBytes: 150);

        Assert.Equal(["a.mp4", "b.mp4"], evicted.Select(f => Path.GetFileName(f.FullName)));
    }

    [Fact]
    public void SelectQuotaEvictionsIsNoOpWhenNoQuotaSet()
    {
        var files = new List<FileInfo> { new(WriteFile("a.mp4", 100, DateTime.UtcNow)) };

        Assert.Empty(StorageManager.SelectQuotaEvictions(files, quotaBytes: null));
        Assert.Empty(StorageManager.SelectQuotaEvictions(files, quotaBytes: 0));
    }

    [Fact]
    public void PruneEmptyDirectoriesRemovesEmptyLeavesButKeepsAncestorsOfNonEmptyOnes()
    {
        // Two hour folders share the same day/month/year ancestor — only the empty leaf should go;
        // the shared day folder survives because its other child (12/) still has a file in it.
        var emptyHour = Path.Combine(_root, "cam-1", "main", "2026", "08", "08", "11");
        var nonEmptyHour = Path.Combine(_root, "cam-1", "main", "2026", "08", "08", "12");
        Directory.CreateDirectory(emptyHour);
        Directory.CreateDirectory(nonEmptyHour);
        File.WriteAllText(Path.Combine(nonEmptyHour, "segment.mp4"), "data");

        // A second camera with nothing but empty folders anywhere under it — proves the bottom-up
        // cascade removes multiple nested levels in one pass, not just the immediate leaf.
        var fullyEmptyTree = Path.Combine(_root, "cam-2", "main", "2026", "08", "08", "09");
        Directory.CreateDirectory(fullyEmptyTree);

        StorageManager.PruneEmptyDirectories(_root);

        Assert.False(Directory.Exists(emptyHour));
        Assert.True(Directory.Exists(Path.Combine(_root, "cam-1", "main", "2026", "08", "08")));
        Assert.True(Directory.Exists(nonEmptyHour));
        Assert.True(File.Exists(Path.Combine(nonEmptyHour, "segment.mp4")));
        Assert.False(Directory.Exists(Path.Combine(_root, "cam-2")));
    }
}
