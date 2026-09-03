using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>Covers CredentialScrubber.Scrub — the guard that keeps a camera's RTSP username/password
/// out of log files. ffmpeg echoes the full input URL (credentials and all) into its own stderr, in
/// the connection banner and in 401/403 failure lines that RecordingSession/MotionSession/
/// SubLiveSession/VisionSession escalate to Warning.</summary>
public class CredentialScrubberTests
{
    [Theory]
    [InlineData("rtsp://admin:p4ssw0rd@192.168.1.50:554/cam/realmonitor?channel=1",
                "rtsp://***@192.168.1.50:554/cam/realmonitor?channel=1")]
    [InlineData("Input #0, rtsp, from 'rtsp://admin:s3cr3t@10.0.0.1:554/Streaming/Channels/101':",
                "Input #0, rtsp, from 'rtsp://***@10.0.0.1:554/Streaming/Channels/101':")]
    [InlineData("rtsp://admin:s3cr3t@10.0.0.1:554/x: Server returned 401 Unauthorized (authorization failed)",
                "rtsp://***@10.0.0.1:554/x: Server returned 401 Unauthorized (authorization failed)")]
    [InlineData("http://user:pw@host/onvif/device_service", "http://***@host/onvif/device_service")]
    public void RedactsUserInfoFromUrlsInText(string input, string expected)
        => Assert.Equal(expected, CredentialScrubber.Scrub(input));

    [Fact]
    public void RedactsAPasswordThatContainsAnUnencodedAtSign()
        => Assert.Equal("rtsp://***@host:554/live",
            CredentialScrubber.Scrub("rtsp://admin:p@ss@host:554/live"));

    [Fact]
    public void RedactsAPercentEncodedPassword()
        => Assert.Equal("rtsp://***@host:554/live",
            CredentialScrubber.Scrub("rtsp://admin:p%40ss%3A1@host:554/live"));

    [Theory]
    [InlineData("rtsp://192.168.1.50:554/cam/realmonitor?channel=1")]     // no credentials
    [InlineData("rtsp://192.168.1.50:554/live@main")]                     // @ only in the path
    [InlineData("frame=  123 fps= 25 q=-1.0 size=    1024kB time=00:00:05")] // ordinary ffmpeg line
    [InlineData("Stream #0:0: Video: h264 (Main), yuvj420p, 1920x1080")]
    public void LeavesTextWithoutEmbeddedCredentialsUntouched(string input)
        => Assert.Equal(input, CredentialScrubber.Scrub(input));

    [Fact]
    public void NullOrEmptyInputReturnsEmpty()
    {
        Assert.Equal("", CredentialScrubber.Scrub(null));
        Assert.Equal("", CredentialScrubber.Scrub(""));
    }
}
