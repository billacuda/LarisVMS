using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Covers RecordingSession's M18 branch: a camera with no enabled Privacy zone keeps the original
/// zero-CPU-cost `-c copy` pipeline exactly as before this feature existed; one with at least one
/// switches to a real filter+encode. Options() is the same for every test except the two fields under
/// test, so a change to one and not the other would fail loudly here.
/// </summary>
public class RecordingSessionPrivacyMaskTests
{
    private static RecordingSessionOptions Options(IReadOnlyList<string>? filters = null, string? encoder = null) =>
        new("ffmpeg", "rtsp://camera/stream", @"C:\Recordings\cam", PrivacyMaskFilters: filters, VideoEncoder: encoder);

    [Fact]
    public void NoPrivacyZonesMeansPlainCopy()
    {
        var args = RecordingSession.BuildCodecArgs(Options());

        Assert.Equal(["-c", "copy"], args);
    }

    [Fact]
    public void NoPrivacyZonesMeansNoDecodeHwaccel()
    {
        Assert.Empty(RecordingSession.BuildDecodeArgs(Options(encoder: "h264_nvenc")));
    }

    [Fact]
    public void APrivacyZoneSwitchesToFilterAndEncodeWithAudioCopy()
    {
        var args = RecordingSession.BuildCodecArgs(Options(
            filters: ["drawbox=x=iw*0.1:y=ih*0.1:w=iw*0.2:h=ih*0.2:color=black:t=fill"], encoder: "libx264"));

        Assert.Equal([
            "-vf", "drawbox=x=iw*0.1:y=ih*0.1:w=iw*0.2:h=ih*0.2:color=black:t=fill",
            "-c:v", "libx264", "-preset", "veryfast",
            "-c:a", "copy"
        ], args);
    }

    [Fact]
    public void MissingVideoEncoderFallsBackToLibx264RatherThanStayingOnCopy()
    {
        var args = RecordingSession.BuildCodecArgs(Options(filters: ["drawbox=x=iw*0.1:y=ih*0.1:w=iw*0.1:h=ih*0.1:color=black:t=fill"]));

        Assert.NotEqual(["-c", "copy"], args);
        Assert.Contains("libx264", args);
    }

    // Confirmed live (real Intel/NVIDIA hardware): pairing decode-side -hwaccel with the plain CPU
    // drawbox filter this pipeline uses made ffmpeg fail immediately on every attempt — the camera
    // never left Connecting/Backoff, no mask (or any output at all) ever appeared. BuildDecodeArgs
    // is deliberately always empty now, for every encoder, regardless of PrivacyMaskFilters — see
    // its own doc comment for the full explanation.
    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("h264_qsv")]
    [InlineData("h264_amf")]
    [InlineData("libx264")]
    public void NoEncoderEverGetsADecodeHwaccelFlag(string encoder)
    {
        Assert.Empty(RecordingSession.BuildDecodeArgs(Options(
            filters: ["drawbox=x=iw*0.1:y=ih*0.1:w=iw*0.1:h=ih*0.1:color=black:t=fill"], encoder: encoder)));
    }

    [Fact]
    public void AmfGetsNoPreset()
    {
        var codecArgs = RecordingSession.BuildCodecArgs(Options(
            filters: ["drawbox=x=iw*0.1:y=ih*0.1:w=iw*0.1:h=ih*0.1:color=black:t=fill"], encoder: "h264_amf"));
        Assert.DoesNotContain("-preset", codecArgs);
    }
}
