using LarisVMS.Core;

namespace LarisVMS.Tests;

/// <summary>A server on either OS must get the file name out of a node's path on the other OS.</summary>
public sealed class ForeignPathTests
{
    [Theory]
    [InlineData(@"D:\exports\cam-1\clip.mp4", "clip.mp4")]
    [InlineData(@"\\nas\rec\cam-1\clip.mp4", "clip.mp4")]
    [InlineData("/var/lib/larisvms/exports/clip.mp4", "clip.mp4")]
    [InlineData("/mnt/nas/rec/mixed\\clip.mp4", "clip.mp4")]
    [InlineData("clip.mp4", "clip.mp4")]
    [InlineData(@"D:\exports\", "")]
    public void GetsTheFileNameFromWindowsAndLinuxPaths(string path, string expected) =>
        Assert.Equal(expected, ForeignPath.GetFileName(path));

    [Theory]
    [InlineData(@"D:\exports\clip.h264.mp4", "clip.h264")]
    [InlineData("/x/clip", "clip")]
    [InlineData("/x/.hidden", ".hidden")]
    public void DropsTheLastExtension(string path, string expected) =>
        Assert.Equal(expected, ForeignPath.GetFileNameWithoutExtension(path));
}
