using LarisVMS.Core.Enums;
using LarisVMS.Vision.Tracking;

namespace LarisVMS.Tests;

/// <summary>
/// Covers <see cref="ByteTrackOptions.ForFamily"/> and, more importantly, the failure it exists to
/// prevent: a new-track threshold sitting above the configured detection confidence, so an object
/// whose score never reaches that threshold can never start a track however the confidence setting
/// is tuned. On a live deployment that read as AI detection working all day and going silent after
/// dark — a moving vehicle at night scores around 0.3-0.5, below the old hardcoded 0.6, while
/// already-tracked static objects kept their boxes via the second association pass.
/// </summary>
public sealed class ByteTrackOptionsTests
{
    [Fact]
    public void TheDefaultConfidenceReproducesTheOriginalPaperValues()
    {
        // A deployment left on the 0.5 default must land exactly where it was before the gates were
        // anchored to confidence, so this change is a no-op for anyone who hasn't tuned it.
        var options = ByteTrackOptions.ForFamily(DetectionModelFamily.YoloX, 0.5);

        Assert.Equal(0.5f, options.TrackThreshold);
        Assert.Equal(0.6f, options.HighThreshold, 5);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.7)]
    public void NewTrackThresholdTracksTheConfiguredConfidence(double confidence)
    {
        // The bug in one assertion: whatever the operator sets, creating a track must not require a
        // score meaningfully above it. Before this, HighThreshold was 0.6 regardless — so at a
        // configured 0.1 an object scoring 0.4 was detected, reported to the tracker, and silently
        // never given a track.
        var options = ByteTrackOptions.ForFamily(DetectionModelFamily.YoloX, confidence);

        Assert.Equal((float)confidence, options.TrackThreshold, 5);
        Assert.Equal((float)confidence + 0.1f, options.HighThreshold, 5);
        Assert.True(options.HighThreshold <= confidence + 0.1f + 1e-5,
            "creating a track must not demand a score far above the configured confidence");
    }

    [Fact]
    public void DFineGetsTheSameConfidenceAnchoringAsYoloX()
    {
        // D-FINE's DETR-style scores run higher than YOLOX's objectness x class product, which is why
        // the old fixed gates never visibly bit for it — but the same stacking bug applied, so it is
        // anchored the same way rather than left on the old defaults.
        var options = ByteTrackOptions.ForFamily(DetectionModelFamily.DFine, 0.25);

        Assert.Equal(0.25f, options.TrackThreshold, 5);
        Assert.Equal(0.35f, options.HighThreshold, 5);
    }

    [Theory]
    [InlineData(0.0, 0.05)]
    [InlineData(-1.0, 0.05)]
    [InlineData(1.0, 0.9)]
    [InlineData(5.0, 0.9)]
    public void ExtremeConfidencesAreClampedToSomethingTheTrackerCanUse(double confidence, double expected)
    {
        // At 0 the tracker would promote pure noise into tracks; above ~0.9 nothing could ever start
        // one, which is the original bug in its most extreme form.
        var options = ByteTrackOptions.ForFamily(DetectionModelFamily.YoloX, confidence);

        Assert.Equal((float)expected, options.TrackThreshold, 5);
    }

    [Fact]
    public void YoloXKeepsItsFusedScoreAssociation()
    {
        // Anchoring the thresholds must not quietly drop the rest of the YOLOX tuning.
        var options = ByteTrackOptions.ForFamily(DetectionModelFamily.YoloX, 0.3);

        Assert.True(options.FuseScore);
        Assert.Equal(0.8f, options.MatchThreshold);
    }

    [Fact]
    public void AnObjectScoringBelowTheOldFixedGateNowGetsATrack()
    {
        // End to end through the tracker itself, at the score a moving vehicle actually produces at
        // night. Under the old fixed 0.6 this returned nothing on every frame — no track, so no box
        // and no span — which is exactly what the deployment showed after dark.
        var tracker = new ByteTracker(ByteTrackOptions.ForFamily(DetectionModelFamily.YoloX, 0.25));

        var f1 = tracker.Update([FakeDetection.Box(100, 100, 60, 40, confidence: 0.42)]);
        var f2 = tracker.Update([FakeDetection.Box(130, 102, 60, 40, confidence: 0.38)]);

        Assert.NotEmpty(f2);
        Assert.Equal(f1[0].Id, f2[0].Id);
    }
}
