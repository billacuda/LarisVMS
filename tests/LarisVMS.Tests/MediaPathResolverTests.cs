using LarisVMS.Node;

namespace LarisVMS.Tests;

/// <summary>
/// The node's media endpoints must serve a segment whose path is under either this node's primary
/// storage root or its archive root (archive storage phase 2) — and nothing else, including a
/// traversal or a wrong camera id.
/// </summary>
public class MediaPathResolverTests
{
    private const string StorageRoot = @"E:\rec";
    private const string ArchiveRoot = @"D:\archive";
    private static readonly Guid Cam = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ResolvesAPathUnderThePrimaryRoot()
    {
        var full = Path.GetFullPath($@"{StorageRoot}\cam-{Cam}\main\2026\09\06\seg.mp4");
        var ok = MediaPathResolver.TryResolve(full, Cam, StorageRoot, ArchiveRoot, out var main, out var thumbs, out var snaps);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath($@"{StorageRoot}\cam-{Cam}\thumbs"), thumbs);
        Assert.Equal(Path.GetFullPath($@"{StorageRoot}\cam-{Cam}\snapshots"), snaps);
        Assert.StartsWith(Path.GetFullPath($@"{StorageRoot}\cam-{Cam}\main"), main);
    }

    [Fact]
    public void ResolvesAPathUnderTheArchiveRoot()
    {
        var full = Path.GetFullPath($@"{ArchiveRoot}\cam-{Cam}\main\2026\09\06\seg.mp4");
        var ok = MediaPathResolver.TryResolve(full, Cam, StorageRoot, ArchiveRoot, out _, out var thumbs, out _);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath($@"{ArchiveRoot}\cam-{Cam}\thumbs"), thumbs);
    }

    [Fact]
    public void RejectsAPathForADifferentCamera()
    {
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var full = Path.GetFullPath($@"{StorageRoot}\cam-{other}\main\seg.mp4");
        Assert.False(MediaPathResolver.IsAllowed(full, Cam, StorageRoot, ArchiveRoot));
    }

    [Fact]
    public void RejectsATraversalOutOfTheCameraDirectory()
    {
        var full = Path.GetFullPath($@"{StorageRoot}\cam-{Cam}\main\..\..\secret.mp4");
        Assert.False(MediaPathResolver.IsAllowed(full, Cam, StorageRoot, ArchiveRoot));
    }

    [Fact]
    public void ResolvesAnArchiveShapedPathEvenWithNoArchiveRootConfigured()
    {
        // The path is shaped like this camera's recording directory, so the out-of-root fallback
        // resolves it — the caller's File.Exists check is what actually gates whether it's served.
        var full = Path.GetFullPath($@"{ArchiveRoot}\cam-{Cam}\main\seg.mp4");
        Assert.True(MediaPathResolver.IsAllowed(full, Cam, StorageRoot, archiveRoot: null));
    }

    [Fact]
    public void ResolvesAPathUnderNeitherRootWhenItIsShapedLikeThisCamerasRecordingDirectory()
    {
        // Footage recorded while the node's storage root was temporarily pointed at a local drive,
        // then changed back — the path is under neither the current primary nor archive root.
        var full = Path.GetFullPath($@"D:\temp-during-a-test\cam-{Cam}\main\2026\09\06\seg.mp4");
        var ok = MediaPathResolver.TryResolve(full, Cam, StorageRoot, ArchiveRoot, out var main, out var thumbs, out var snaps);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath($@"D:\temp-during-a-test\cam-{Cam}\main") + Path.DirectorySeparatorChar, main);
        Assert.Equal(Path.GetFullPath($@"D:\temp-during-a-test\cam-{Cam}\thumbs"), thumbs);
        Assert.Equal(Path.GetFullPath($@"D:\temp-during-a-test\cam-{Cam}\snapshots"), snaps);
    }

    [Fact]
    public void StillRejectsAPathThatIsNotShapedLikeAnyCamerasRecordingDirectory()
    {
        var full = Path.GetFullPath($@"D:\somewhere\else\secret.mp4");
        Assert.False(MediaPathResolver.IsAllowed(full, Cam, StorageRoot, ArchiveRoot));
    }

    [Fact]
    public void TheOutOfRootFallbackStillRequiresThisCameraSId()
    {
        var other = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var full = Path.GetFullPath($@"D:\temp\cam-{other}\main\seg.mp4");
        Assert.False(MediaPathResolver.IsAllowed(full, Cam, StorageRoot, ArchiveRoot));
    }
}
