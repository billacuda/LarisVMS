using LarisVMS.Core.Security;

namespace LarisVMS.Tests;

/// <summary>
/// A camera's reported stream address is used on the camera's own host, keeping the reported port
/// and path, so the node never sends the camera's password to a machine the camera named.
/// </summary>
public class StreamUriHostTests
{
    [Theory]
    [InlineData("rtsp://192.168.1.10:554/stream1", "192.168.1.10", "rtsp://192.168.1.10:554/stream1")]
    [InlineData("rtsp://203.0.113.66:554/stream1?ch=2", "192.168.1.10", "rtsp://192.168.1.10:554/stream1?ch=2")]
    [InlineData("rtsp://10.0.0.5:8554/live", "cam1.example.com", "rtsp://cam1.example.com:8554/live")]
    [InlineData("rtsps://10.0.0.5:322/live", "cam1.example.com", "rtsps://cam1.example.com:322/live")]
    [InlineData("rtsp://10.0.0.5/live", "fd00::5", "rtsp://[fd00::5]/live")]
    [InlineData("rtsp://10.0.0.5/live", "[fd00::5]", "rtsp://[fd00::5]/live")]
    [InlineData("rtsp://10.0.0.5/live", "", "rtsp://10.0.0.5/live")]
    public void TheStreamGoesToTheCamerasOwnHost(string reported, string cameraHost, string expected) =>
        Assert.Equal(expected, StreamUris.OnCameraHost(new Uri(reported), cameraHost).ToString());
}
