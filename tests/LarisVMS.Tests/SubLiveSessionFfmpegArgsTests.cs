using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Guards SubLiveSession's ffmpeg command. Its live fMP4 output has to match RecordingSession's own
/// live tee leg exactly — same movflags, same frag_duration/min_frag_duration — because both are
/// read by the identical Mp4BoxScanner drain and the identical live-view.js MSE consumer. A flag
/// added to one leg and not mirrored here (or vice versa) would give the Sub path a different
/// fragment cadence, which live-view.js's latency controller reads as sawtoothing drift.
/// Same "assert the exact arg list" approach as MotionSessionTests.
/// </summary>
public class SubLiveSessionFfmpegArgsTests
{
    private static SubLiveSessionOptions Options() => new("ffmpeg", "rtsp://camera/sub");

    [Fact]
    public void PlainCopyRemuxToFragmentedMp4OnStdout()
    {
        var args = SubLiveSession.BuildFfmpegArgs(Options());

        Assert.Equal([
            "-nostdin", "-rtsp_transport", "tcp", "-timeout", "5000000",
            "-i", "rtsp://camera/sub",
            "-map", "0:v", "-map", "0:a?",
            "-c", "copy",
            "-f", "mp4", "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "500000",
            "-min_frag_duration", "200000",
            "pipe:1"
        ], args);
    }

    [Fact]
    public void FragmentCadenceMatchesRecordingSessionsLivePipeLeg()
    {
        var subArgs = SubLiveSession.BuildFfmpegArgs(Options());
        var pipeLeg = RecordingSession.BuildTeeOutputs(60, "out.mp4").Split('|').Single(l => l.Contains("pipe:1"));

        // Whatever RecordingSession's live leg asks for, the Sub path asks for the same.
        Assert.Contains("frag_duration=500000", pipeLeg);
        Assert.Contains("min_frag_duration=200000", pipeLeg);
        Assert.Equal("500000", subArgs[subArgs.ToList().IndexOf("-frag_duration") + 1]);
        Assert.Equal("200000", subArgs[subArgs.ToList().IndexOf("-min_frag_duration") + 1]);
    }
}
