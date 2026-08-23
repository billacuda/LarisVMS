using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// The guards standing between "File.Exists said no" and "delete this segment's database row". An
/// unreachable SMB share answers false for every path rather than throwing, so without these a blip
/// during the reconcile sweep would drop every row this node owns for footage still safely on disk.
/// </summary>
public class StorageHealthTests
{
    [Fact]
    public void AReachableDirectoryPassesTheProbe()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.True(StorageHealth.CanReachStorage(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void TheProbeLeavesNoFileBehind()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            StorageHealth.CanReachStorage(dir);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AnUnreachableDirectoryFailsTheProbe()
    {
        // Stands in for the real hazard (an SMB share that has gone away): the path simply isn't
        // there, and every File.Exists under it would answer false rather than throw.
        Assert.False(StorageHealth.CanReachStorage(Path.Combine(Path.GetTempPath(), "larisvms-does-not-exist-" + Guid.NewGuid().ToString("N"))));
    }

    [Theory]
    // Below the absolute floor, any proportion is plausible — a node with a handful of segments can
    // legitimately have most of them evicted between sweeps.
    [InlineData(10, 10, false)]
    [InlineData(49, 49, false)]
    // At real volumes, a few percent missing is normal orphan cleanup...
    [InlineData(2, 1000, false)]
    [InlineData(100, 1000, false)]
    // ...but past the threshold it's a storage fault, not that many genuine deletions.
    [InlineData(101, 1000, true)]
    [InlineData(62841, 62841, true)] // the real-world catastrophe this guard exists to prevent
    public void ImplausibleMissingCountsAreRecognized(int missing, int known, bool expected)
    {
        Assert.Equal(expected, StorageHealth.IsImplausibleMissingCount(missing, known));
    }
}
