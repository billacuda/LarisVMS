using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// StorageManager's side of server-directed retention: which paths it will agree to delete, and the
/// persisted queue of deletions it hasn't reported yet.
/// </summary>
public class StorageManagerExpiredSegmentTests : IDisposable
{
    private static readonly Guid Camera = Guid.Parse("858a905f-17b4-4f94-9296-2cc03ebd9780");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "LarisVMSExpiredSegmentTests_" + Guid.NewGuid());

    public StorageManagerExpiredSegmentTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(@"\\files1\nvr$\LarisVMS\cam-858a905f-17b4-4f94-9296-2cc03ebd9780\main\2026\09\02\23\20260902T233215Z.mp4",
        @"\\files1\nvr$\LarisVMS", @"\\files1\nvr$\LarisVMS\cam-858a905f-17b4-4f94-9296-2cc03ebd9780\main")]
    [InlineData(@"D:\Video\cam-858a905f-17b4-4f94-9296-2cc03ebd9780\sub\2026\09\02\a.MP4",
        @"D:\Video", @"D:\Video\cam-858a905f-17b4-4f94-9296-2cc03ebd9780\sub")]
    public void AcceptsTheRecordersOwnLayoutOnAnyRoot(string path, string expectedRoot, string expectedStreamDir)
    {
        var layout = StorageManager.TryGetSegmentLayout(path, Camera);

        Assert.NotNull(layout);
        Assert.Equal(expectedRoot, layout.Value.RootDir, ignoreCase: true);
        Assert.Equal(expectedStreamDir, layout.Value.StreamDir, ignoreCase: true);
    }

    [Theory]
    [InlineData(@"D:\Video\cam-858a905f-17b4-4f94-9296-2cc03ebd9780\main\2026\a.txt")]               // not a segment file
    [InlineData(@"D:\Video\cam-00000000-0000-0000-0000-000000000001\main\2026\a.mp4")]               // another camera
    [InlineData(@"D:\Video\cam-858a905f-17b4-4f94-9296-2cc03ebd9780\thumbs\2026\a.mp4")]             // not a stream folder
    [InlineData(@"D:\Video\cam-858a905f-17b4-4f94-9296-2cc03ebd9780\main\..\..\Windows\a.mp4")]      // relative segments
    [InlineData(@"D:\Video\cam-858a905f-17b4-4f94-9296-2cc03ebd9780\main.mp4")]                       // no stream folder at all
    [InlineData(@"cam-858a905f-17b4-4f94-9296-2cc03ebd9780\main\2026\a.mp4")]                         // nothing above cam-{id}
    [InlineData("")]
    public void RefusesAnythingElse(string path)
    {
        Assert.Null(StorageManager.TryGetSegmentLayout(path, Camera));
    }

    [Fact]
    public void PendingDeletionsSurviveARestartAndClearWhenEmpty()
    {
        var path = Path.Combine(_dir, "pending.json");
        List<string> pending = [@"D:\Video\cam-x\main\a.mp4", @"\\nas\share\cam-x\main\b.mp4"];

        StorageManager.SavePendingDeletions(path, pending);
        Assert.Equal(pending, StorageManager.LoadPendingDeletions(path));

        StorageManager.SavePendingDeletions(path, []);
        Assert.False(File.Exists(path));
        Assert.Empty(StorageManager.LoadPendingDeletions(path));
    }

    [Fact]
    public void ACorruptPendingFileStartsEmpty()
    {
        var path = Path.Combine(_dir, "pending.json");
        File.WriteAllText(path, "{not json");

        Assert.Empty(StorageManager.LoadPendingDeletions(path));
    }
}
