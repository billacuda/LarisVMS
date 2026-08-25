using LarisVMS.Vision.Tracking;

namespace LarisVMS.Tests;

/// <summary>
/// Integration tests for the full <see cref="ByteTracker.Update{T}"/> loop. These are the tests
/// that matter most: unit tests on the Kalman filter and IoU math prove the pieces are correct in
/// isolation, but the entire reason to choose ByteTrack over a simpler tracker is what happens
/// when detections are momentarily weak or missing, and that only shows up end to end. Ported from
/// aitest's own Aitest.Vision.Tests.Tracking.ByteTrackerTests (MIT), unchanged apart from its
/// namespace/using — see ByteKalmanFilterTests' own doc comment for why this restores coverage the
/// production code shipped without in this repo.
/// </summary>
public sealed class ByteTrackerTests
{
    [Fact]
    public void StableObject_KeepsSameIdAcrossFrames()
    {
        var tracker = new ByteTracker();

        var f1 = tracker.Update([FakeDetection.Box(100, 100, 50, 80)]);
        var f2 = tracker.Update([FakeDetection.Box(105, 102, 50, 80)]);
        var f3 = tracker.Update([FakeDetection.Box(110, 104, 50, 80)]);

        Assert.Single(f1);
        Assert.Single(f2);
        Assert.Single(f3);
        Assert.Equal(f1[0].Id, f2[0].Id);
        Assert.Equal(f2[0].Id, f3[0].Id);
    }

    [Fact]
    public void PartialOcclusion_LowConfidenceFrameKeepsSameId()
    {
        // This is the behaviour that distinguishes ByteTrack from a tracker that only considers
        // detections above a single confidence threshold. A partially occluded object typically
        // keeps producing detections, just weaker ones -- ByteTrack's second association pass
        // matches those against the unmatched track by IoU instead of discarding them, so the ID
        // survives the occlusion instead of being dropped and reissued on the far side.
        var tracker = new ByteTracker();

        var f1 = tracker.Update([FakeDetection.Box(100, 100, 50, 80, confidence: 0.9)]);
        var f2 = tracker.Update([FakeDetection.Box(110, 100, 50, 80, confidence: 0.9)]);

        // Confidence drops below TrackThreshold (0.5) but stays above the 0.1 floor -- exactly
        // the partial-occlusion case. Box continues along the same trajectory.
        var f3 = tracker.Update([FakeDetection.Box(120, 100, 50, 80, confidence: 0.3)]);

        var f4 = tracker.Update([FakeDetection.Box(130, 100, 50, 80, confidence: 0.9)]);

        Assert.Single(f1);
        Assert.Single(f2);
        var trackedId = f1[0].Id;

        // The low-confidence frame must still be reported -- with the SAME id -- not silently
        // dropped. If ByteTrack behaved like a naive threshold filter, f3 would be empty and f4
        // would carry a new id.
        Assert.Single(f3);
        Assert.Equal(trackedId, f3[0].Id);

        Assert.Single(f4);
        Assert.Equal(trackedId, f4[0].Id);
    }

    [Fact]
    public void FullyMissedFrame_WithinTrackBuffer_RecoversOriginalId()
    {
        // No detection at all for one frame (e.g. total occlusion for an instant), well within
        // the default 30-frame track buffer. The track should coast via Kalman prediction while
        // Lost, then be re-matched by IoU when the object reappears.
        var tracker = new ByteTracker();

        var f1 = tracker.Update([FakeDetection.Box(100, 100, 50, 80)]);
        var f2 = tracker.Update([FakeDetection.Box(110, 100, 50, 80)]);
        var f3 = tracker.Update(Array.Empty<FakeDetection>()); // object briefly invisible
        var f4 = tracker.Update([FakeDetection.Box(130, 100, 50, 80)]);

        Assert.Single(f1);
        Assert.Single(f2);
        Assert.Empty(f3);

        Assert.Single(f4);
        Assert.Equal(f1[0].Id, f4[0].Id);
    }

    [Fact]
    public void LostBeyondTrackBuffer_GetsNewIdOnReturn()
    {
        // The mirror image of the previous test: once a track has been Lost for longer than the
        // buffer, it must be retired -- otherwise "track buffer" would be meaningless and old
        // identities would haunt the tracker indefinitely.
        var options = new ByteTrackOptions { TrackBuffer = 3, FrameRate = 30 };
        var tracker = new ByteTracker(options);

        var f1 = tracker.Update([FakeDetection.Box(100, 100, 50, 80)]);
        Assert.Single(f1);
        var originalId = f1[0].Id;

        // Miss enough frames to exceed the buffer.
        for (var i = 0; i < 6; i++)
        {
            tracker.Update(Array.Empty<FakeDetection>());
        }

        // The old track is now fully Removed rather than Lost, so this is a brand new identity,
        // not a resumed one -- and a brand new identity needs a second confirming frame before
        // it is reported, same as any first-time track (the frameId==1 fast path only applies to
        // the very first frame of the tracker's life). One detection is not enough by itself.
        var reappear1 = tracker.Update([FakeDetection.Box(100, 100, 50, 80)]);
        Assert.Empty(reappear1);

        var reappear2 = tracker.Update([FakeDetection.Box(100, 100, 50, 80)]);

        Assert.Single(reappear2);
        Assert.NotEqual(originalId, reappear2[0].Id);
    }

    [Fact]
    public void CrossingObjects_DoNotSwapIdentitiesAtClosestApproach()
    {
        // Two objects moving toward each other along the same row, fully overlapping at the
        // midpoint frame before separating again. This is the classic tracker failure mode: a
        // purely position-based matcher (nearest centroid to previous frame) swaps identities at
        // the crossing because after the swap, "closest to previous position" points at the
        // wrong object. Using each track's own Kalman-predicted trajectory for matching, rather
        // than raw previous position, is what should keep this correct.
        var tracker = new ByteTracker();

        // Step size is deliberately small relative to the 60px box: a 10px step against a 60px
        // box keeps frame-to-frame IoU around 0.7 (cost ~0.3), comfortably clear of
        // MatchThreshold's 0.8 cutoff. A larger step that lands the cost close to the threshold
        // makes the test's own consecutive-frame association flaky on floating point rounding,
        // independent of whether the crossing itself is handled correctly.
        const int steps = 21;
        const int frameStep = 10;
        var (leftId, rightId) = (0, 0);

        for (var t = 0; t < steps; t++)
        {
            var leftX = t * frameStep;              // moving right: 0, 10, 20, ...
            var rightX = 200 - (t * frameStep);      // moving left: 200, 190, 180, ... (crosses ~t=10)

            var detections = tracker.Update([
                FakeDetection.Box(leftX, 100, 60, 60, confidence: 0.9),
                FakeDetection.Box(rightX, 100, 60, 60, confidence: 0.9),
            ]);

            Assert.Equal(2, detections.Count);

            if (t == 0)
            {
                // Establish which assigned id started on the left vs. the right.
                var byX = detections.OrderBy(d => d.BoundingBox.Left).ToList();
                leftId = byX[0].Id!.Value;
                rightId = byX[1].Id!.Value;
                continue;
            }

            // From here on, identity must be recovered by ID, not by position -- position is
            // exactly what becomes ambiguous at the crossing.
            var left = detections.SingleOrDefault(d => d.Id == leftId);
            var right = detections.SingleOrDefault(d => d.Id == rightId);

            Assert.NotNull(left);
            Assert.NotNull(right);

            // The detection carrying leftId must still be near the true left-object trajectory
            // (and likewise for right), regardless of which one is now geometrically on the left
            // side of the frame after the crossing.
            AssertNear(left!.BoundingBox.Left, leftX, tolerance: 30);
            AssertNear(right!.BoundingBox.Left, rightX, tolerance: 30);
        }
    }

    [Fact]
    public void SimultaneousObjects_NeverPresentSameFrame_GetDistinctIds()
    {
        var tracker = new ByteTracker();

        var f1 = tracker.Update([
            FakeDetection.Box(0, 0, 50, 50, confidence: 0.9),
            FakeDetection.Box(500, 500, 50, 50, confidence: 0.9),
        ]);

        Assert.Equal(2, f1.Count);
        Assert.NotEqual(f1[0].Id, f1[1].Id);
    }

    private static void AssertNear(int actual, int expected, int tolerance)
    {
        Assert.True(
            Math.Abs(actual - expected) <= tolerance,
            $"expected {actual} to be within {tolerance} of {expected}");
    }
}
