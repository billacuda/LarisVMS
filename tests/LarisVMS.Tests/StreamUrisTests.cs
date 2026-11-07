using LarisVMS.Core.Security;

namespace LarisVMS.Tests;

/// <summary>Stream addresses the recorder node hands to ffmpeg: only RTSP, so a camera can't point
/// ffmpeg at the node's own files or at internal hosts.</summary>
public class StreamUrisTests
{
    [Theory]
    [InlineData("rtsp://192.168.1.20:554/stream1")]
    [InlineData("RTSP://camera.local/live")]
    [InlineData("rtsps://camera.local:322/live")]
    public void RtspAddressesAreAllowed(string uri) => Assert.True(StreamUris.IsAllowed(uri));

    [Theory]
    [InlineData("file:///C:/ProgramData/LarisVMS/recordings/a.mp4")]
    [InlineData("file:C:/Windows/win.ini")]
    [InlineData("concat:a.mp4|b.mp4")]
    [InlineData("subfile:,start,0,end,0,:C:/x.mp4")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("https://intranet.local/")]
    [InlineData("tcp://10.0.0.1:80")]
    [InlineData("/recordings/a.mp4")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElseIsRefused(string? uri) => Assert.False(StreamUris.IsAllowed(uri));
}
