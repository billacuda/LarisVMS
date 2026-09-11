using LarisVMS.Media;

namespace LarisVMS.Tests;

/// <summary>
/// Guards the ffmpeg -f tee output spec. Both legs feed MSE — the pipe leg feeds live view, the
/// recorded files feed playback — so both need default_base_moof. It was originally set only on the
/// pipe leg, which shipped a bug that reported nothing anywhere: Chrome accepted the append of a
/// recorded segment, fired updateend normally, and produced no buffered range at all, so a playback
/// tile just rendered nothing while looking identical to one still loading. Nothing about that is
/// visible from C#, hence asserting on the flags themselves.
/// </summary>
public class RecordingSessionTeeOutputTests
{
    [Fact]
    public void BothLegsCarryTheMseRequiredMuxerFlags()
    {
        var tee = RecordingSession.BuildTeeOutputs(60, @"C:\Recordings\cam\%Y\%m\%d\%H\%Y%m%dT%H%M%SZ.mp4");

        var legs = tee.Split('|');
        Assert.Equal(2, legs.Length);
        Assert.All(legs, leg => Assert.Contains("+default_base_moof", leg));
        // frag_keyframe+empty_moov matter independently: they're what make a segment truncated
        // mid-write (power loss) still a playable file rather than a zero-length moov.
        Assert.All(legs, leg => Assert.Contains("+frag_keyframe", leg));
        Assert.All(legs, leg => Assert.Contains("+empty_moov", leg));
    }

    [Fact]
    public void SegmentLegEscapesTheWindowsDriveColonForTeeBracketSyntax()
    {
        var tee = RecordingSession.BuildTeeOutputs(60, @"C:\Recordings\cam\file.mp4");

        // Tee parses ':' as its own option separator, so an unescaped drive letter truncates the
        // path and ffmpeg fails to open the output.
        Assert.Contains(@"C\:/Recordings/cam/file.mp4", tee);
    }

    [Fact]
    public void SegmentLegUsesTheConfiguredSegmentLength()
    {
        var tee = RecordingSession.BuildTeeOutputs(30, "out.mp4");

        Assert.Contains("segment_time=30", tee);
    }

    // The live pipe leg emits a fragment at least every 500ms (but not more often than every 200ms)
    // even mid-GOP, so the browser's SourceBuffer.buffered.end() advances smoothly instead of in
    // camera-keyframe-sized jumps — see RecordingSession.LiveFragDurationMicros and live-view.js's
    // latency controller.
    [Fact]
    public void PipeLegRequestsSubKeyframeFragmentDuration()
    {
        var pipeLeg = RecordingSession.BuildTeeOutputs(60, "out.mp4").Split('|').Single(l => l.Contains("pipe:1"));

        Assert.Contains("frag_duration=500000", pipeLeg);
        Assert.Contains("min_frag_duration=200000", pipeLeg);
    }

    // The recording leg must NOT get frag_duration: playback (playback-player.js, Mp4FragmentIndexer)
    // depends on the recorded segment files fragmenting on keyframes only.
    [Fact]
    public void SegmentLegNeverGetsFragDuration()
    {
        var segmentLeg = RecordingSession.BuildTeeOutputs(60, @"C:\rec\out.mp4").Split('|').Single(l => l.Contains("f=segment"));

        Assert.DoesNotContain("frag_duration", segmentLeg);
    }
}
