using LarisVMS.NodeUpdater;

namespace LarisVMS.Tests;

/// <summary>Covers UpdaterLogic — the argument-parsing and backup/move/cleanup file-swap logic pulled
/// out of LarisVMS.NodeUpdater/Program.cs's top-level statements specifically so it has a callable
/// surface a test can exercise directly. The sc.exe-dependent pieces (service Stopped polling, start,
/// failure-recovery re-apply) stay inline in Program.cs and aren't covered here — this environment has
/// no real Windows Service to poll, per the task's own guidance to skip/guard those.</summary>
public class NodeUpdaterLogicTests
{
    [Fact]
    public void ParseArgsReadsAllThreeFlags()
    {
        var parsed = UpdaterLogic.ParseArgs(["--new", @"C:\staged\LarisVMS.Node.exe", "--current", @"C:\install\LarisVMS.Node.exe", "--service", "CustomService"]);

        Assert.Equal(@"C:\staged\LarisVMS.Node.exe", parsed.NewBinary);
        Assert.Equal(@"C:\install\LarisVMS.Node.exe", parsed.CurrentBinary);
        Assert.Equal("CustomService", parsed.ServiceName);
    }

    [Fact]
    public void ParseArgsDefaultsServiceNameWhenNotGiven()
    {
        var parsed = UpdaterLogic.ParseArgs(["--new", @"C:\staged\LarisVMS.Node.exe", "--current", @"C:\install\LarisVMS.Node.exe"]);

        Assert.Equal(UpdaterLogic.DefaultServiceName, parsed.ServiceName);
        Assert.Equal("LarisVMSNode", parsed.ServiceName);
    }

    [Fact]
    public void ParseArgsReturnsNullsWhenNewOrCurrentIsMissing()
    {
        var parsed = UpdaterLogic.ParseArgs(["--service", "SomeService"]);

        Assert.Null(parsed.NewBinary);
        Assert.Null(parsed.CurrentBinary);
    }

    [Fact]
    public void ParseArgsIgnoresUnknownFlagsAndTrailingDanglingFlag()
    {
        // The trailing "--new" with no following value must not throw or consume past args.Length —
        // ParseArgs's loop bound (args.Length - 1) exists specifically to guard this.
        var parsed = UpdaterLogic.ParseArgs(["--bogus", "value", "--current", @"C:\install\LarisVMS.Node.exe", "--new"]);

        Assert.Null(parsed.NewBinary);
        Assert.Equal(@"C:\install\LarisVMS.Node.exe", parsed.CurrentBinary);
    }

    [Fact]
    public void TrySwapBinaryMovesNewOverCurrentAndRemovesBackup()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LarisVMS.Tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var currentPath = Path.Combine(tempDir, "LarisVMS.Node.exe");
            var newPath = Path.Combine(tempDir, "staged.exe");
            File.WriteAllText(currentPath, "old binary contents");
            File.WriteAllText(newPath, "new binary contents");

            var ok = UpdaterLogic.TrySwapBinary(newPath, currentPath, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal("new binary contents", File.ReadAllText(currentPath));
            Assert.False(File.Exists(newPath)); // File.Move removed the source
            Assert.False(File.Exists(currentPath + ".bak")); // backup cleaned up on success
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TrySwapBinaryWorksEvenWhenCurrentBinaryDoesNotYetExist()
    {
        // A brand-new install has nothing at currentBinary yet the very first time this ever runs
        // (not a realistic path for the auto-update flow, which only ever swaps an already-running
        // binary, but the backup step's own "if it exists" guard should still hold up on its own).
        var tempDir = Path.Combine(Path.GetTempPath(), "LarisVMS.Tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var currentPath = Path.Combine(tempDir, "LarisVMS.Node.exe");
            var newPath = Path.Combine(tempDir, "staged.exe");
            File.WriteAllText(newPath, "new binary contents");

            var ok = UpdaterLogic.TrySwapBinary(newPath, currentPath, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal("new binary contents", File.ReadAllText(currentPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TrySwapBinaryReturnsFalseAndErrorWhenNewBinaryDoesNotExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LarisVMS.Tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var currentPath = Path.Combine(tempDir, "LarisVMS.Node.exe");
            var newPath = Path.Combine(tempDir, "does-not-exist.exe");
            File.WriteAllText(currentPath, "old binary contents");

            var ok = UpdaterLogic.TrySwapBinary(newPath, currentPath, out var error);

            Assert.False(ok);
            Assert.NotNull(error);
            // The failed move must not have touched the original binary.
            Assert.Equal("old binary contents", File.ReadAllText(currentPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
