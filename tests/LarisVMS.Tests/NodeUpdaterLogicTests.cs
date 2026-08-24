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
        // The trailing "--new" with no following value must not throw or consume past args.Length.
        // The loop now runs to args.Length (so a valueless flag in the final position is still seen —
        // --restart-only needs that); the per-case "when i + 1 < args.Length" guards are what keep a
        // dangling value-taking flag safe.
        var parsed = UpdaterLogic.ParseArgs(["--bogus", "value", "--current", @"C:\install\LarisVMS.Node.exe", "--new"]);

        Assert.Null(parsed.NewBinary);
        Assert.Equal(@"C:\install\LarisVMS.Node.exe", parsed.CurrentBinary);
    }

    [Fact]
    public void ParseArgsReadsRestartOnlyAsTheFinalArgument()
    {
        // Specifically the last-position case: a valueless flag there was invisible under the old
        // args.Length - 1 bound, and --restart-only is normally passed exactly like this.
        var parsed = UpdaterLogic.ParseArgs(["--service", "LarisVMSNode", "--restart-only"]);

        Assert.True(parsed.RestartOnly);
        Assert.Equal("LarisVMSNode", parsed.ServiceName);
        Assert.Null(parsed.NewBinary);
        Assert.Null(parsed.CurrentBinary);
    }

    [Fact]
    public void ParseArgsDefaultsRestartOnlyToFalseForAnOrdinaryUpdate()
    {
        var parsed = UpdaterLogic.ParseArgs(["--new", @"C:\staged\n.exe", "--current", @"C:\install\n.exe"]);

        Assert.False(parsed.RestartOnly);
    }

    [Fact]
    public void ParseArgsReadsTheOptionalVisionPairAlongsideTheRequiredOne()
    {
        var parsed = UpdaterLogic.ParseArgs([
            "--new", @"C:\staged\LarisVMS.Node.exe", "--current", @"C:\install\LarisVMS.Node.exe",
            "--new-vision", @"C:\staged\LarisVMS.Vision.Service.exe", "--current-vision", @"C:\install\LarisVMS.Vision.Service.exe"
        ]);

        Assert.Equal(@"C:\staged\LarisVMS.Vision.Service.exe", parsed.NewVisionBinary);
        Assert.Equal(@"C:\install\LarisVMS.Vision.Service.exe", parsed.CurrentVisionBinary);
        // The required pair is unaffected by the optional one riding alongside it.
        Assert.Equal(@"C:\staged\LarisVMS.Node.exe", parsed.NewBinary);
        Assert.Equal(@"C:\install\LarisVMS.Node.exe", parsed.CurrentBinary);
    }

    [Fact]
    public void ParseArgsLeavesVisionPairNullWhenNotGiven()
    {
        var parsed = UpdaterLogic.ParseArgs(["--new", @"C:\staged\LarisVMS.Node.exe", "--current", @"C:\install\LarisVMS.Node.exe"]);

        Assert.Null(parsed.NewVisionBinary);
        Assert.Null(parsed.CurrentVisionBinary);
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
