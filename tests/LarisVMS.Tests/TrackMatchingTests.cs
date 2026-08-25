using LarisVMS.Vision.Tracking;

namespace LarisVMS.Tests;

/// <summary>Ported from aitest's own Aitest.Vision.Tests.Tracking.TrackMatchingTests (MIT),
/// unchanged apart from its namespace/using — see ByteKalmanFilterTests' own doc comment for why
/// this restores coverage the production code shipped without in this repo.</summary>
public sealed class TrackMatchingTests
{
    private static int _nextTestTrackId;

    private static STrack MakeTrack(float x, float y, float w, float h, float score = 0.9f)
    {
        var track = new STrack([x, y, w, h], score, classId: 0);
        // Activate so GetTlbr reflects the given box rather than an uninitialized Kalman mean.
        track.Activate(new ByteKalmanFilter(), frameId: 1, trackId: ++_nextTestTrackId);
        return track;
    }

    [Fact]
    public void IouDistance_IdenticalBoxes_IsZero()
    {
        var a = MakeTrack(0, 0, 100, 100);
        var b = MakeTrack(0, 0, 100, 100);

        var cost = TrackMatching.IouDistance([a], [b]);

        Assert.Equal(0f, cost[0, 0], precision: 5);
    }

    [Fact]
    public void IouDistance_DisjointBoxes_IsOne()
    {
        var a = MakeTrack(0, 0, 50, 50);
        var b = MakeTrack(1000, 1000, 50, 50);

        var cost = TrackMatching.IouDistance([a], [b]);

        Assert.Equal(1f, cost[0, 0], precision: 5);
    }

    [Fact]
    public void IouDistance_KnownOverlap_MatchesHandComputedValue()
    {
        // a = [0,0,100,100], area 10000. b = [50,50,100,100], area 10000.
        // Intersection = [50,50]-[100,100] = 50x50 = 2500. Union = 20000-2500 = 17500.
        // IoU = 2500/17500 = 1/7.
        var a = MakeTrack(0, 0, 100, 100);
        var b = MakeTrack(50, 50, 100, 100);

        var cost = TrackMatching.IouDistance([a], [b]);

        Assert.Equal(1f - (1f / 7f), cost[0, 0], precision: 5);
    }

    [Fact]
    public void FuseScore_WeightsCostByDetectionConfidence()
    {
        var track = MakeTrack(0, 0, 100, 100);
        var strongDetection = MakeTrack(0, 0, 100, 100, score: 0.95f);
        var weakDetection = MakeTrack(0, 0, 100, 100, score: 0.3f);

        var costStrong = TrackMatching.IouDistance([track], [strongDetection]);
        var costWeak = TrackMatching.IouDistance([track], [weakDetection]);

        TrackMatching.FuseScore(costStrong, [strongDetection]);
        TrackMatching.FuseScore(costWeak, [weakDetection]);

        // Same geometry, different confidence -- fusing must make the weak match cost more,
        // otherwise a low-confidence detection would be indistinguishable from a strong one.
        Assert.True(costWeak[0, 0] > costStrong[0, 0]);
    }

    [Fact]
    public void LinearAssignment_MatchesClosestPairsAndRespectsThreshold()
    {
        // Two tracks, two detections. Track 0 sits on detection 0; track 1 sits far from both,
        // so it must go unmatched under a strict threshold.
        var trackA = MakeTrack(0, 0, 100, 100);
        var trackB = MakeTrack(2000, 2000, 100, 100);
        var detectionA = MakeTrack(5, 5, 100, 100);
        var detectionB = MakeTrack(9000, 9000, 100, 100);

        var cost = TrackMatching.IouDistance([trackA, trackB], [detectionA, detectionB]);

        var (matches, unmatchedTracks, unmatchedDetections) =
            TrackMatching.LinearAssignment(cost, trackCount: 2, detectionCount: 2, threshold: 0.5f);

        Assert.Single(matches);
        Assert.Equal((0, 0), matches[0]);
        Assert.Contains(1, unmatchedTracks);
        Assert.Contains(1, unmatchedDetections);
    }

    [Fact]
    public void LinearAssignment_EmptyDetections_AllTracksUnmatched()
    {
        var track = MakeTrack(0, 0, 100, 100);

        var (matches, unmatchedTracks, unmatchedDetections) =
            TrackMatching.LinearAssignment(new float[1, 0], trackCount: 1, detectionCount: 0, threshold: 0.5f);

        Assert.Empty(matches);
        Assert.Equal([0], unmatchedTracks);
        Assert.Empty(unmatchedDetections);
    }
}
