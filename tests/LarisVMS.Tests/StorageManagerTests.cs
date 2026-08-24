using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// Exercises StorageManager's eviction-decision helpers against a real temp directory tree — safe
/// to run anywhere (never touches a live node's actual recordings) while still proving the
/// filesystem-facing pieces (age filtering, oldest-first quota selection, bottom-up empty-directory
/// pruning) work against a real filesystem rather than a mocked one.
/// </summary>
public class StorageManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LarisVMSStorageManagerTests_" + Guid.NewGuid());

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
    public void SelectMissingPathsReturnsOnlyPathsThatNoLongerExistOnDisk()
    {
        var present = WriteFile("present.mp4", 10, DateTime.UtcNow);
        var neverExisted = Path.Combine(_root, "never-existed.mp4");
        var wasDeleted = WriteFile("was-deleted.mp4", 10, DateTime.UtcNow);
        File.Delete(wasDeleted);

        var missing = StorageManager.SelectMissingPaths([present, neverExisted, wasDeleted]);

        Assert.Equal([neverExisted, wasDeleted], missing);
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

    // ── FindOrphanedCameraMainDirs (reassigned-away camera footage cleanup) ─

    [Fact]
    public void FindsACameraFolderThatIsNoLongerAssignedToThisNode()
    {
        var assignedId = Guid.NewGuid();
        var orphanedId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_root, $"cam-{assignedId}", "main"));
        var orphanedMain = Directory.CreateDirectory(Path.Combine(_root, $"cam-{orphanedId}", "main")).FullName;

        var found = StorageManager.FindOrphanedCameraMainDirs(_root, [assignedId]);

        Assert.Equal([(orphanedId, orphanedMain)], found);
    }

    [Fact]
    public void ReturnsNothingWhenEveryCameraFolderIsStillAssigned()
    {
        var cameraId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_root, $"cam-{cameraId}", "main"));

        var found = StorageManager.FindOrphanedCameraMainDirs(_root, [cameraId]);

        Assert.Empty(found);
    }

    [Fact]
    public void IgnoresAnOrphanedCameraFolderWithNoMainSubdirectory()
    {
        // A folder that's just "cam-{id}/thumbs" with no "main" (fully evicted already, or a
        // partial/corrupt leftover) shouldn't be treated as something to sweep.
        Directory.CreateDirectory(Path.Combine(_root, $"cam-{Guid.NewGuid()}", "thumbs"));

        var found = StorageManager.FindOrphanedCameraMainDirs(_root, []);

        Assert.Empty(found);
    }

    [Fact]
    public void ReturnsEmptyWhenTheStorageRootDoesNotExist()
    {
        var found = StorageManager.FindOrphanedCameraMainDirs(Path.Combine(_root, "does-not-exist"), []);

        Assert.Empty(found);
    }

    // ── FindMatchingThumbnails (M7 pass 2) ──────────────────────────────────

    [Fact]
    public void FindMatchingThumbnailsFindsEveryBucketOffsetFileForAnEvictedSegmentsStem()
    {
        var mainDir = Path.Combine(_root, "cam-1", "main");
        var thumbsDir = Path.Combine(_root, "cam-1", "thumbs");
        var mainFile = Path.Combine(mainDir, "2026", "08", "09", "14", "20260809T140000Z.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(mainFile)!);
        File.WriteAllText(mainFile, "data");

        var thumbDir = Path.Combine(thumbsDir, "2026", "08", "09", "14");
        Directory.CreateDirectory(thumbDir);
        var thumb1 = Path.Combine(thumbDir, "20260809T140000Z_o00.jpg");
        var thumb2 = Path.Combine(thumbDir, "20260809T140000Z_o05.jpg");
        File.WriteAllText(thumb1, "jpg");
        File.WriteAllText(thumb2, "jpg");

        var found = StorageManager.FindMatchingThumbnails(mainDir, thumbsDir, mainFile);

        Assert.Equal(2, found.Count);
        Assert.Contains(thumb1, found);
        Assert.Contains(thumb2, found);
    }

    [Fact]
    public void FindMatchingThumbnailsReturnsEmptyWhenNoThumbsDirectoryExists()
    {
        var mainDir = Path.Combine(_root, "cam-1", "main");
        var thumbsDir = Path.Combine(_root, "cam-1", "thumbs"); // never created
        var mainFile = Path.Combine(mainDir, "2026", "08", "09", "14", "20260809T140000Z.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(mainFile)!);
        File.WriteAllText(mainFile, "data");

        var found = StorageManager.FindMatchingThumbnails(mainDir, thumbsDir, mainFile);

        Assert.Empty(found);
    }

    [Fact]
    public void FindMatchingThumbnailsDoesNotMatchADifferentlyNamedNeighboringSegment()
    {
        // Stem-prefix precision: "...T140000Z" must not also match a thumbnail belonging to
        // "...T140000Z-extra" or a segment recorded one second later, "...T140001Z".
        var mainDir = Path.Combine(_root, "cam-1", "main");
        var thumbsDir = Path.Combine(_root, "cam-1", "thumbs");
        var mainFile = Path.Combine(mainDir, "2026", "08", "09", "14", "20260809T140000Z.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(mainFile)!);
        File.WriteAllText(mainFile, "data");

        var thumbDir = Path.Combine(thumbsDir, "2026", "08", "09", "14");
        Directory.CreateDirectory(thumbDir);
        var ownThumb = Path.Combine(thumbDir, "20260809T140000Z_o00.jpg");
        var neighborThumb = Path.Combine(thumbDir, "20260809T140001Z_o00.jpg");
        File.WriteAllText(ownThumb, "jpg");
        File.WriteAllText(neighborThumb, "jpg");

        var found = StorageManager.FindMatchingThumbnails(mainDir, thumbsDir, mainFile);

        Assert.Equal([ownThumb], found);
    }

    // ── FindMatchingSnapshotImages (object detection plan decision 10) ──────

    [Fact]
    public void FindMatchingSnapshotImagesFindsEverySpanFileForAnEvictedSegmentsStem()
    {
        var mainDir = Path.Combine(_root, "cam-1", "main");
        var snapshotsDir = Path.Combine(_root, "cam-1", "snapshots");
        var mainFile = Path.Combine(mainDir, "2026", "08", "09", "14", "20260809T140000Z.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(mainFile)!);
        File.WriteAllText(mainFile, "data");

        // Two different detected objects in the same segment produce two different owning spans,
        // each with its own cached crop — both must be found and cleaned up together.
        var snapshotDir = Path.Combine(snapshotsDir, "2026", "08", "09", "14");
        Directory.CreateDirectory(snapshotDir);
        var snapshot1 = Path.Combine(snapshotDir, "20260809T140000Z_span101.jpg");
        var snapshot2 = Path.Combine(snapshotDir, "20260809T140000Z_span102.jpg");
        File.WriteAllText(snapshot1, "jpg");
        File.WriteAllText(snapshot2, "jpg");

        var found = StorageManager.FindMatchingSnapshotImages(mainDir, snapshotsDir, mainFile);

        Assert.Equal(2, found.Count);
        Assert.Contains(snapshot1, found);
        Assert.Contains(snapshot2, found);
    }

    [Fact]
    public void FindMatchingSnapshotImagesReturnsEmptyWhenNoSnapshotsDirectoryExists()
    {
        var mainDir = Path.Combine(_root, "cam-1", "main");
        var snapshotsDir = Path.Combine(_root, "cam-1", "snapshots"); // never created
        var mainFile = Path.Combine(mainDir, "2026", "08", "09", "14", "20260809T140000Z.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(mainFile)!);
        File.WriteAllText(mainFile, "data");

        var found = StorageManager.FindMatchingSnapshotImages(mainDir, snapshotsDir, mainFile);

        Assert.Empty(found);
    }

    [Fact]
    public void FindMatchingSnapshotImagesDoesNotMatchADifferentlyNamedNeighboringSegment()
    {
        var mainDir = Path.Combine(_root, "cam-1", "main");
        var snapshotsDir = Path.Combine(_root, "cam-1", "snapshots");
        var mainFile = Path.Combine(mainDir, "2026", "08", "09", "14", "20260809T140000Z.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(mainFile)!);
        File.WriteAllText(mainFile, "data");

        var snapshotDir = Path.Combine(snapshotsDir, "2026", "08", "09", "14");
        Directory.CreateDirectory(snapshotDir);
        var ownSnapshot = Path.Combine(snapshotDir, "20260809T140000Z_span1.jpg");
        var neighborSnapshot = Path.Combine(snapshotDir, "20260809T140001Z_span1.jpg");
        File.WriteAllText(ownSnapshot, "jpg");
        File.WriteAllText(neighborSnapshot, "jpg");

        var found = StorageManager.FindMatchingSnapshotImages(mainDir, snapshotsDir, mainFile);

        Assert.Equal([ownSnapshot], found);
    }
}
