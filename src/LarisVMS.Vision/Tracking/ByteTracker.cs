// Ported from ByteTrack — https://github.com/FoundationVision/ByteTrack
// SPDX-License-Identifier: MIT
// Copyright (c) 2021 Yifu Zhang
//
// C# port of BYTETracker::update. Structure follows yolox/tracker/byte_tracker.py, which is the
// canonical implementation; the C++ deploy variant was used to resolve details the Python leaves
// to numpy broadcasting.
//
// Ported verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Tracking\ByteTracker.cs).

using SkiaSharp;
using YoloDotNet.Models.Interfaces;

namespace LarisVMS.Vision.Tracking;

public sealed class ByteTrackOptions
{
    /// <summary>Detections at or above this score enter the first association pass.</summary>
    public float TrackThreshold { get; init; } = 0.5f;

    /// <summary>Score required to start a brand new track. Deliberately stricter than
    /// <see cref="TrackThreshold"/>, so a marginal detection can extend a track but not create one.</summary>
    public float HighThreshold { get; init; } = 0.6f;

    /// <summary>Maximum 1-IoU cost accepted in the first association pass.</summary>
    public float MatchThreshold { get; init; } = 0.8f;

    /// <summary>Frames a lost track is kept alive before removal, scaled by frame rate.</summary>
    public int TrackBuffer { get; init; } = 30;

    public int FrameRate { get; init; } = 30;

    /// <summary>Length of the retained motion trail, used only for drawing.</summary>
    public int TailLength { get; init; } = 30;

    /// <summary>
    /// Weight detection confidence into the association cost. The reference enables this for
    /// every dataset except MOT20, whose crowd density makes it counterproductive.
    /// </summary>
    public bool FuseScore { get; init; } = true;
}

/// <summary>
/// ByteTrack multi-object tracker.
///
/// The idea that distinguishes it from SORT is the second association pass: detections too weak to
/// be trusted on their own (below <see cref="ByteTrackOptions.TrackThreshold"/>) are still matched
/// against tracks that went unmatched in the first pass. A partially occluded object usually keeps
/// producing weak detections, so this is what preserves identity through occlusion instead of
/// re-issuing a new ID on the far side.
/// </summary>
public sealed class ByteTracker
{
    private readonly ByteTrackOptions _options;
    private readonly ByteKalmanFilter _filter = new();
    private readonly int _maxTimeLost;

    private readonly List<STrack> _tracked = [];
    private readonly List<STrack> _lost = [];
    private readonly Dictionary<int, List<SKPoint>> _tails = [];

    private int _frameId;

    // Owned per-tracker rather than a static counter on STrack: each camera gets its own
    // ByteTracker instance, and a process-wide counter would let IDs collide across cameras and
    // would need Interlocked to be safe under concurrent pipelines. ByteTracker.Update is only
    // ever called from the one pipeline thread that owns this tracker, so a plain int is enough.
    private int _nextTrackId;

    public ByteTracker(ByteTrackOptions? options = null)
    {
        _options = options ?? new ByteTrackOptions();
        _maxTimeLost = (int)(_options.FrameRate / 30.0 * _options.TrackBuffer);
    }

    private int NextTrackId() => ++_nextTrackId;

    /// <summary>
    /// Assigns stable track IDs to this frame's detections, writing them into
    /// <see cref="IDetection.Id"/> and <see cref="IDetection.Tail"/>.
    ///
    /// Writing into those properties rather than returning a parallel structure is what lets
    /// YoloDotNet's Draw() render IDs and motion trails unchanged -- it is the same contract
    /// its own SortTracker uses.
    /// </summary>
    public IReadOnlyList<T> Update<T>(IReadOnlyList<T> detections)
        where T : class, IDetection
    {
        _frameId++;

        var activated = new List<STrack>();
        var refound = new List<STrack>();
        var newlyLost = new List<STrack>();
        var removed = new List<STrack>();

        // Step 1 - split detections by confidence.
        var high = new List<STrack>();
        var low = new List<STrack>();

        for (var i = 0; i < detections.Count; i++)
        {
            var detection = detections[i];
            var score = (float)detection.Confidence;
            var box = detection.BoundingBox;

            Span<float> tlwh = [box.Left, box.Top, box.Width, box.Height];
            var track = new STrack(tlwh, score, detection.Label?.Index ?? 0) { DetectionIndex = i };

            if (score >= _options.TrackThreshold)
            {
                high.Add(track);
            }
            else if (score > 0.1f)
            {
                low.Add(track);
            }
        }

        // Tracks awaiting a second corroborating frame are handled separately, so a single
        // spurious detection cannot produce a reported track.
        var unconfirmed = new List<STrack>();
        var tracked = new List<STrack>();

        foreach (var track in _tracked)
        {
            if (track.IsActivated)
            {
                tracked.Add(track);
            }
            else
            {
                unconfirmed.Add(track);
            }
        }

        // Step 2 - first association, against high confidence detections.
        var pool = new List<STrack>(tracked);
        pool.AddRange(_lost);

        STrack.MultiPredict(pool, _filter);

        var cost = TrackMatching.IouDistance(pool, high);
        if (_options.FuseScore)
        {
            TrackMatching.FuseScore(cost, high);
        }

        var (matches, unmatchedTracks, unmatchedHigh) =
            TrackMatching.LinearAssignment(cost, pool.Count, high.Count, _options.MatchThreshold);

        foreach (var (trackIndex, detectionIndex) in matches)
        {
            var track = pool[trackIndex];
            var detection = high[detectionIndex];

            if (track.State == TrackState.Tracked)
            {
                track.Update(_filter, detection, _frameId);
                activated.Add(track);
            }
            else
            {
                track.ReActivate(_filter, detection, _frameId, newTrackId: null);
                refound.Add(track);
            }

            track.DetectionIndex = detection.DetectionIndex;
        }

        // Step 3 - second association, against low confidence detections. This is the part that
        // carries identity through occlusion.
        var stillTracked = unmatchedTracks
            .Where(i => pool[i].State == TrackState.Tracked)
            .Select(i => pool[i])
            .ToList();

        var lowCost = TrackMatching.IouDistance(stillTracked, low);
        var (lowMatches, unmatchedLowTracks, _) =
            TrackMatching.LinearAssignment(lowCost, stillTracked.Count, low.Count, 0.5f);

        foreach (var (trackIndex, detectionIndex) in lowMatches)
        {
            var track = stillTracked[trackIndex];
            var detection = low[detectionIndex];

            if (track.State == TrackState.Tracked)
            {
                track.Update(_filter, detection, _frameId);
                activated.Add(track);
            }
            else
            {
                track.ReActivate(_filter, detection, _frameId, newTrackId: null);
                refound.Add(track);
            }

            track.DetectionIndex = detection.DetectionIndex;
        }

        foreach (var index in unmatchedLowTracks)
        {
            var track = stillTracked[index];
            if (track.State != TrackState.Lost)
            {
                track.MarkLost();
                newlyLost.Add(track);
            }
        }

        // Step 4 - unconfirmed tracks get one chance against what is left.
        var remainingHigh = unmatchedHigh.Select(i => high[i]).ToList();
        var unconfirmedCost = TrackMatching.IouDistance(unconfirmed, remainingHigh);
        if (_options.FuseScore)
        {
            TrackMatching.FuseScore(unconfirmedCost, remainingHigh);
        }

        var (unconfirmedMatches, unmatchedUnconfirmed, stillUnmatchedHigh) =
            TrackMatching.LinearAssignment(unconfirmedCost, unconfirmed.Count, remainingHigh.Count, 0.7f);

        foreach (var (trackIndex, detectionIndex) in unconfirmedMatches)
        {
            var track = unconfirmed[trackIndex];
            track.Update(_filter, remainingHigh[detectionIndex], _frameId);
            track.DetectionIndex = remainingHigh[detectionIndex].DetectionIndex;
            activated.Add(track);
        }

        foreach (var index in unmatchedUnconfirmed)
        {
            var track = unconfirmed[index];
            track.MarkRemoved();
            removed.Add(track);
        }

        // Step 5 - start tracks for confident leftovers.
        foreach (var index in stillUnmatchedHigh)
        {
            var detection = remainingHigh[index];
            if (detection.Score < _options.HighThreshold)
            {
                continue;
            }

            detection.Activate(_filter, _frameId, NextTrackId());
            activated.Add(detection);
        }

        // Step 6 - retire tracks lost for too long.
        foreach (var track in _lost)
        {
            if (_frameId - track.FrameId > _maxTimeLost)
            {
                track.MarkRemoved();
                removed.Add(track);
            }
        }

        // Step 7 - rebuild the track lists.
        _tracked.RemoveAll(t => t.State != TrackState.Tracked);
        MergeInto(_tracked, activated);
        MergeInto(_tracked, refound);

        _lost.RemoveAll(t => _tracked.Any(x => x.TrackId == t.TrackId));
        MergeInto(_lost, newlyLost);
        _lost.RemoveAll(t => removed.Any(x => x.TrackId == t.TrackId));

        RemoveDuplicates(_tracked, _lost);

        foreach (var track in removed)
        {
            _tails.Remove(track.TrackId);
        }

        // Step 8 - write IDs and trails back onto the caller's detections.
        var output = new List<T>();

        foreach (var track in _tracked)
        {
            if (!track.IsActivated || track.DetectionIndex < 0 || track.DetectionIndex >= detections.Count)
            {
                continue;
            }

            var detection = detections[track.DetectionIndex];
            detection.Id = track.TrackId;
            detection.Tail = UpdateTail(track);
            output.Add(detection);
        }

        return output;
    }

    private List<SKPoint> UpdateTail(STrack track)
    {
        if (!_tails.TryGetValue(track.TrackId, out var tail))
        {
            tail = [];
            _tails[track.TrackId] = tail;
        }

        var centre = new SKPoint(
            track.Tlwh[0] + (track.Tlwh[2] * 0.5f),
            track.Tlwh[1] + (track.Tlwh[3] * 0.5f));

        tail.Add(centre);

        if (tail.Count > _options.TailLength)
        {
            tail.RemoveRange(0, tail.Count - _options.TailLength);
        }

        return tail;
    }

    private static void MergeInto(List<STrack> destination, List<STrack> source)
    {
        foreach (var track in source)
        {
            if (!destination.Any(t => t.TrackId == track.TrackId))
            {
                destination.Add(track);
            }
        }
    }

    /// <summary>
    /// Drops near-identical pairs across the tracked and lost lists, keeping whichever has the
    /// longer history. Without this, a track that is briefly lost and immediately re-detected can
    /// end up represented twice.
    /// </summary>
    private static void RemoveDuplicates(List<STrack> a, List<STrack> b)
    {
        var cost = TrackMatching.IouDistance(a, b);
        var duplicateA = new HashSet<int>();
        var duplicateB = new HashSet<int>();

        for (var i = 0; i < a.Count; i++)
        {
            for (var j = 0; j < b.Count; j++)
            {
                if (cost[i, j] >= 0.15f)
                {
                    continue;
                }

                var ageA = a[i].FrameId - a[i].StartFrame;
                var ageB = b[j].FrameId - b[j].StartFrame;

                if (ageA > ageB)
                {
                    duplicateB.Add(j);
                }
                else
                {
                    duplicateA.Add(i);
                }
            }
        }

        foreach (var index in duplicateA.OrderByDescending(x => x))
        {
            a.RemoveAt(index);
        }

        foreach (var index in duplicateB.OrderByDescending(x => x))
        {
            b.RemoveAt(index);
        }
    }

    /// <summary>Clears all state. Used when the stream or model changes underneath the tracker.</summary>
    public void Reset()
    {
        _tracked.Clear();
        _lost.Clear();
        _tails.Clear();
        _frameId = 0;
    }
}
