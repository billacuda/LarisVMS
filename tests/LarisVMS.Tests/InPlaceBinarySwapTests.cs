using LarisVMS.Core.Update;

namespace LarisVMS.Tests;

public sealed class InPlaceBinarySwapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "laris-swap-" + Guid.NewGuid().ToString("N"));
    private string Current => Path.Combine(_dir, "LarisVMS.Node.exe");
    private string Staged => Path.Combine(_dir, "staged", "LarisVMS.Node.exe");

    public InPlaceBinarySwapTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "staged"));
        File.WriteAllText(Current, "old");
        File.WriteAllText(Staged, "new");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void MovesCurrentAsideAndStagedIntoPlace()
    {
        Assert.True(InPlaceBinarySwap.TrySwap(Staged, Current, out var error), error);

        Assert.Equal("new", File.ReadAllText(Current));
        Assert.Equal("old", File.ReadAllText(Current + InPlaceBinarySwap.OldSuffix));
        Assert.False(File.Exists(Staged));
    }

    [Fact]
    public void ReplacesALeftoverOldFromThePreviousUpdate()
    {
        File.WriteAllText(Current + InPlaceBinarySwap.OldSuffix, "older");

        Assert.True(InPlaceBinarySwap.TrySwap(Staged, Current, out _));
        Assert.Equal("old", File.ReadAllText(Current + InPlaceBinarySwap.OldSuffix));
    }

    [Fact]
    public void RunningStyleOpenFileCanStillBeSwapped()
    {
        // A running image is open without FileShare.Write but allows rename (FileShare.Delete) —
        // the reason a running exe can be moved aside but not overwritten.
        using (new FileStream(Current, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.True(InPlaceBinarySwap.TrySwap(Staged, Current, out var error), error);
        }
        Assert.Equal("new", File.ReadAllText(Current));
    }

    [Fact]
    public void PutsCurrentBackWhenTheStagedMoveFails()
    {
        File.Delete(Staged);

        Assert.False(InPlaceBinarySwap.TrySwap(Staged, Current, out var error));
        Assert.NotNull(error);
        Assert.Equal("old", File.ReadAllText(Current));
    }

    [Fact]
    public void CleanUpRemovesOnlyOldExes()
    {
        File.WriteAllText(Current + InPlaceBinarySwap.OldSuffix, "x");
        var other = Path.Combine(_dir, "notes.old");
        File.WriteAllText(other, "keep");

        InPlaceBinarySwap.CleanUp(_dir);

        Assert.False(File.Exists(Current + InPlaceBinarySwap.OldSuffix));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(Current));
    }
}
