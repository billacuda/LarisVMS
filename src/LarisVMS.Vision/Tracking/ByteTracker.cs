// Ported from ByteTrack — https://github.com/FoundationVision/ByteTrack
// SPDX-License-Identifier: MIT
// Copyright (c) 2021 Yifu Zhang
//
// C# port of BYTETracker::update. Structure follows yolox/tracker/byte_tracker.py, which is the
// canonical implementation; the C++ deploy variant was used to resolve details the Python leaves
// to numpy broadcasting.
//
// Ported verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Tracking\ByteTracker.cs).

using LarisVMS.Core.Enums;
using SkiaSharp;
using YoloDotNet.Models.Interfaces;

namespace LarisVMS.Vision.Tracking;

public sealed class ByteTrackOptions
{
    /// <summary>Per-detection-family tracker tuning, anchored to the deployment's own configured
    /// detection confidence.
    ///
    /// These used to be hardcoded at ByteTrack's paper defaults (0.5 / 0.6), which are the values the
    /// reference implementation pairs with a detector run at ~0.1 — the paper's whole design is that
    /// the *tracker* does the gating, not the detector. LarisVMS instead runs the detector at the
    /// operator's Detection.Confidence and then applied the paper's gates on top, so the two stacked:
    /// with a 0.6 new-track threshold, any object whose score never reached 0.6 could never start a
    /// track no matter how the confidence setting was tuned. That is invisible in daylight, where a
    /// vehicle scores well above it, and total after dark, where a moving vehicle is blurred and dim
    /// and sits around 0.3-0.5 — an object already being tracked survives on the second association
    /// pass and keeps its box, while nothing new is ever picked up. Confirmed live: lowering
    /// Detection.Confidence to 0.1 changed nothing, because this gate is downstream of it.
    ///
    /// So the gates now follow the confidence the operator actually set, keeping ByteTrack's own
    /// relationship between them (the reference derives its new-track threshold as
    /// <c>track_thresh + 0.1</c>). A deployment left on the old 0.5 default lands back on exactly
    /// 0.5 / 0.6 and behaves as before.</summary>
    public static ByteTrackOptions ForFamily(DetectionModelFamily family, double detectionConfidence)
    {
        // Clamped so a confidence at either extreme can't produce a nonsensical pair: at 0 the tracker
        // would accept literally anything as a new track, and above ~0.9 nothing could ever start one.
        var trackThreshold = (float)Math.Clamp(detectionConfidence, 0.05, 0.9);

        return family switch
        {
            // Explicit rather than `new ByteTrackOptions()` so a change to the field defaults can't
            // silently move YOLOX's tracker off these.
            DetectionModelFamily.YoloX => new ByteTrackOptions
            {
                TrackThreshold = trackThreshold,
                HighThreshold = trackThreshold + 0.1f,
                MatchThreshold = 0.8f,
                FuseScore = true,
            },
            _ => new ByteTrackOptions
            {
                TrackThreshold = trackThreshold,
                HighThreshold = trackThreshold + 0.1f,
            },
        };
    }

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

    // Buffered-IoU fallback (TrackMatching.BufferedIouDistance): box enlargements tried, in order, for
    // pairs the plain overlap passes left unmatched, and the most cost (1 - buffered IoU) accepted.
    private static readonly float[] Buffers = [0.5f, 1.0f];
    private const float BufferedMatchThreshold = 0.7f;

    /// <summary>Running totals for the cadence log: new candidate tracks, those confirmed by a second
    /// frame (and so reported), and those dropped after one frame. Many dropped means objects are
    /// being lost before they can show — usually because detection runs too few frames a second.</summary>
    public int TracksStarted { get; private set; }
    public int TracksConfirmed { get; private set; }
    public int TracksDroppedUnconfirmed { get; private set; }

    /// <summary>Hidden tracks picked up again by step 4c, in total.</summary>
    public int TracksRejoined { get; private set; }

    /// <summary>This frame's rejoins (step 4c): a hidden track that came back into view.
    /// <c>From</c> is the young track folded into it (its id is gone from now on), or 0 when it was
    /// picked up from a fresh detection; <c>FramesHidden</c> is how long it was out of sight.</summary>
    public IReadOnlyList<(int From, int Into, int FramesHidden)> Rejoins => _rejoins;
    private readonly List<(int From, int Into, int FramesHidden)> _rejoins = [];

    /// <summary>
    /// Every track ByteTrack still considers alive after the most recent <see cref="Update{T}"/>: the
    /// ones it emitted this frame plus the ones it is coasting through a brief gap (state Lost, kept
    /// for up to <c>_maxTimeLost</c> frames so the second association pass can re-acquire them
    /// without issuing a new id).
    ///
    /// A caller that keeps its own per-track state (centroid history, a stable label, an "already
    /// snapshotted" marker) must prune against this — not against <see cref="Update{T}"/>'s return
    /// value, which omits a track the instant it misses one detection frame. Pruning on the return
    /// value alone makes a single dropped frame (motion blur on a fast object, a partial occlusion)
    /// look identical to the track ending.
    /// </summary>
    public IReadOnlySet<int> LiveTrackIds
    {
        get
        {
            var ids = new HashSet<int>();
            foreach (var track in _tracked)
            {
                if (track.TrackId != 0) ids.Add(track.TrackId);
            }
            foreach (var track in _lost)
            {
                if (track.TrackId != 0) ids.Add(track.TrackId);
            }
            return ids;
        }
    }

    /// <summary>
    /// Assigns stable track IDs to this frame's detections, writing them into
    /// <see cref="IDetection.Id"/> and <see cref="IDetection.Tail"/>.
    ///
    /// Writing into those properties rather than returning a parallel structure is what lets
    /// YoloDotNet's Draw() render IDs and motion trails unchanged -- it is the same contract
    /// its own SortTracker uses.
    ///
    /// <paramref name="canRejoin"/> (hidden track id, detection index) has the last word on whether
    /// an object coming back into view may be that hidden track (step 4c) — the caller knows its
    /// label and colors. Without it, the same class is enough.
    /// </summary>
    public IReadOnlyList<T> Update<T>(IReadOnlyList<T> detections, Func<int, int, bool>? canRejoin = null)
        where T : class, IDetection
    {
        _frameId++;
        _rejoins.Clear();

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

        // Step 2b - an object that moved further than its own size since the last analysed frame
        // doesn't overlap its prediction at all, and a just-confirmed track's speed estimate is still
        // too small to predict it. Match what's left on enlarged boxes — but only for a track that is
        // either brand new (no reliable speed yet) or already moving, toward a detection ahead of it.
        // A parked object's track never qualifies, so it can't hop onto the car beside it when that
        // car's box flickers into view. Lost tracks never qualify either.
        var leftTracks = unmatchedTracks.Where(i => BufferedMatchAllowed(pool[i])).ToList();
        var leftHigh = unmatchedHigh.ToList();
        foreach (var buffer in Buffers)
        {
            if (leftTracks.Count == 0 || leftHigh.Count == 0) break;
            var tracksLeft = leftTracks.Select(i => pool[i]).ToList();
            var highLeft = leftHigh.Select(i => high[i]).ToList();
            var bufferedCost = TrackMatching.BufferedIouDistance(tracksLeft, highLeft, buffer);
            for (var t = 0; t < tracksLeft.Count; t++)
                for (var d = 0; d < highLeft.Count; d++)
                    if (!AheadOf(tracksLeft[t], highLeft[d])) bufferedCost[t, d] = 1f;
            var (bufferedMatches, _, _) = TrackMatching.LinearAssignment(bufferedCost, tracksLeft.Count, highLeft.Count, BufferedMatchThreshold);
            var usedTracks = new HashSet<int>();
            var usedHigh = new HashSet<int>();
            foreach (var (t, d) in bufferedMatches)
            {
                tracksLeft[t].Update(_filter, highLeft[d], _frameId);
                tracksLeft[t].DetectionIndex = highLeft[d].DetectionIndex;
                activated.Add(tracksLeft[t]);
                usedTracks.Add(leftTracks[t]);
                usedHigh.Add(leftHigh[d]);
            }
            leftTracks.RemoveAll(usedTracks.Contains);
            leftHigh.RemoveAll(usedHigh.Contains);
            unmatchedTracks.RemoveAll(usedTracks.Contains);
            unmatchedHigh.RemoveAll(usedHigh.Contains);
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
                // Seen over a few frames and moving: it has a direction, so it may rejoin (step 4c).
                track.MarkLost(track.FrameId - track.StartFrame >= YoungTrackFrames - 1 && IsMoving(track, RejoinSpeedShare));
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

        // Step 4b - a new track's second frame: it has no speed estimate yet, so a fast object (or one
        // seen at a few frames a second) can have moved further than its own size and not overlap its
        // first box at all. Match what's left on enlarged boxes, same class and similar size only.
        // Confirmed tracks don't get this: their own motion prediction follows a real mover, and on
        // them it only lets a track hop onto the parked car next to it when that car's box flickers.
        var leftUnconfirmed = unmatchedUnconfirmed.ToList();
        var leftRemaining = stillUnmatchedHigh.ToList();
        foreach (var buffer in Buffers)
        {
            if (leftUnconfirmed.Count == 0 || leftRemaining.Count == 0) break;
            var tracksLeft = leftUnconfirmed.Select(i => unconfirmed[i]).ToList();
            var highLeft = leftRemaining.Select(i => remainingHigh[i]).ToList();
            var (bufferedMatches, _, _) = TrackMatching.LinearAssignment(
                TrackMatching.BufferedIouDistance(tracksLeft, highLeft, buffer), tracksLeft.Count, highLeft.Count, BufferedMatchThreshold);
            var usedTracks = new HashSet<int>();
            var usedHigh = new HashSet<int>();
            foreach (var (t, d) in bufferedMatches)
            {
                var track = tracksLeft[t];
                track.Update(_filter, highLeft[d], _frameId);
                track.DetectionIndex = highLeft[d].DetectionIndex;
                activated.Add(track);
                usedTracks.Add(leftUnconfirmed[t]);
                usedHigh.Add(leftRemaining[d]);
            }
            leftUnconfirmed.RemoveAll(usedTracks.Contains);
            leftRemaining.RemoveAll(usedHigh.Contains);
        }
        unmatchedUnconfirmed = leftUnconfirmed;
        stillUnmatchedHigh = leftRemaining;
        TracksConfirmed += unconfirmed.Count - unmatchedUnconfirmed.Count;

        // Step 4c - an object that went behind something (a tree, a post) and comes out further along
        // its line. Nothing above can find its hidden track: after a second or more out of sight at a
        // few frames a second, the prediction rarely overlaps where it reappears. So a track that was
        // moving when lost may take a detection — or a track only a few frames old, its partly hidden
        // first sighting — that lies ahead of it on the same line, at a believable distance, and is
        // about its size. A parked object's track is never eligible.
        var rejoinable = _lost.Where(t => t.State == TrackState.Lost && t.MovingWhenLost
            && _frameId - t.FrameId <= _maxTimeLost).ToList();
        if (rejoinable.Count > 0)
        {
            var byDetection = new Dictionary<int, STrack>();
            foreach (var d in high.Concat(low)) byDetection[d.DetectionIndex] = d;

            // Candidates: (detection, young track it's matched to this frame, or null).
            var candidates = stillUnmatchedHigh.Select(i => (Detection: remainingHigh[i], Young: (STrack?)null, HighIndex: i)).ToList();
            foreach (var young in activated)
            {
                if (young.TrackId == 0 || _frameId - young.StartFrame > YoungTrackFrames) continue;
                if (byDetection.TryGetValue(young.DetectionIndex, out var seen)) candidates.Add((seen, young, -1));
            }

            if (candidates.Count > 0)
            {
                var rejoinCost = new float[rejoinable.Count, candidates.Count];
                for (var t = 0; t < rejoinable.Count; t++)
                {
                    for (var c = 0; c < candidates.Count; c++)
                    {
                        var (detection, young, _) = candidates[c];
                        var allowed = (young is null || young.StartFrame > rejoinable[t].FrameId)
                            && (canRejoin?.Invoke(rejoinable[t].TrackId, detection.DetectionIndex)
                                ?? rejoinable[t].ClassId == detection.ClassId);
                        rejoinCost[t, c] = allowed && RejoinCost(rejoinable[t], detection) is { } fit ? fit : 1f;
                    }
                }

                var (rejoined, _, _) = TrackMatching.LinearAssignment(rejoinCost, rejoinable.Count, candidates.Count, RejoinMaxCost);
                var usedHigh = new HashSet<int>();
                foreach (var (t, c) in rejoined)
                {
                    var hidden = rejoinable[t];
                    var (detection, young, highIndex) = candidates[c];
                    var framesHidden = _frameId - hidden.FrameId;
                    hidden.ReActivate(_filter, detection, _frameId, newTrackId: null);
                    hidden.DetectionIndex = detection.DetectionIndex;
                    refound.Add(hidden);
                    if (young is not null)
                    {
                        activated.Remove(young);
                        young.MarkRemoved();
                        removed.Add(young);
                    }
                    else usedHigh.Add(highIndex);
                    _rejoins.Add((young?.TrackId ?? 0, hidden.TrackId, framesHidden));
                    TracksRejoined++;
                }
                stillUnmatchedHigh.RemoveAll(usedHigh.Contains);
            }
        }

        foreach (var index in unmatchedUnconfirmed)
        {
            var track = unconfirmed[index];
            track.MarkRemoved();
            removed.Add(track);
            TracksDroppedUnconfirmed++;
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
            TracksStarted++;
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

    // A track this many frames old or younger has no reliable speed estimate yet.
    private const int YoungTrackFrames = 3;
    // Moving: centre speed of at least this share of the box's size per frame.
    private const float MovingSpeedShare = 0.05f;
    // Lower for rejoining (step 4c), so a slow walker still qualifies; the caller's canRejoin and
    // the direction gate keep parked objects out.
    private const float RejoinSpeedShare = 0.02f;
    // Step 4c cost is the distance from the predicted spot over the allowed spread; above this, no.
    private const float RejoinMaxCost = 0.99f;

    /// <summary>How well a detection fits a hidden track coming back into view (step 4c), 0 best,
    /// or null when it can't be that object: a different size, behind where it was hidden, off its
    /// line, or further than it could have gone.</summary>
    private float? RejoinCost(STrack hidden, STrack detection)
    {
        var last = hidden.LastSeenTlwh;
        var box = detection.Tlwh;
        if (last[2] <= 0 || last[3] <= 0) return null;

        // About its height; narrower allowed, since it may still be half behind the tree.
        var heightRatio = box[3] / last[3];
        var widthRatio = box[2] / last[2];
        if (heightRatio < 0.6f || heightRatio > 1.67f || widthRatio < 0.35f || widthRatio > 2f) return null;

        var speed = MathF.Sqrt(hidden.LostVx * hidden.LostVx + hidden.LostVy * hidden.LostVy);
        if (speed <= 0) return null;
        var ux = hidden.LostVx / speed;
        var uy = hidden.LostVy / speed;
        var diag = MathF.Sqrt(last[2] * last[2] + last[3] * last[3]);
        var frames = _frameId - hidden.FrameId;

        var dx = box[0] + box[2] / 2f - (last[0] + last[2] / 2f);
        var dy = box[1] + box[3] / 2f - (last[1] + last[3] / 2f);
        var along = dx * ux + dy * uy;
        var side = MathF.Abs(dx * uy - dy * ux);
        var reach = speed * frames;

        // Ahead (it may have slowed or paused behind the tree), not past where it could get to, and
        // close to its line.
        if (along < -0.25f * diag || along > 2.5f * reach + diag) return null;
        if (side > MathF.Max(0.6f * diag, 0.35f * along)) return null;

        // Closest to where it would be at its old speed wins.
        var px = last[0] + last[2] / 2f + hidden.LostVx * frames;
        var py = last[1] + last[3] / 2f + hidden.LostVy * frames;
        var miss = MathF.Sqrt((box[0] + box[2] / 2f - px) * (box[0] + box[2] / 2f - px) + (box[1] + box[3] / 2f - py) * (box[1] + box[3] / 2f - py));
        return MathF.Min(RejoinMaxCost - 0.01f, miss / (2f * (diag + reach)));
    }

    /// <summary>Whether a track may be matched on enlarged boxes (step 2b): currently tracked, and
    /// either young or moving.</summary>
    private bool BufferedMatchAllowed(STrack track) =>
        track.State == TrackState.Tracked && (_frameId - track.StartFrame <= YoungTrackFrames || IsMoving(track));

    private static bool IsMoving(STrack track, float share = MovingSpeedShare)
    {
        if (track.Mean is not { } m) return false;
        var size = Math.Max(m[3], m[2] * m[3]);
        return size > 0 && MathF.Sqrt(m[4] * m[4] + m[5] * m[5]) >= share * size;
    }

    /// <summary>For a moving track, the detection lies ahead of where it was predicted, not behind
    /// it or off to the side; a young track has no direction yet, so anything goes.</summary>
    private static bool AheadOf(STrack track, STrack detection)
    {
        if (!IsMoving(track) || track.Mean is not { } m) return true;
        Span<float> box = stackalloc float[4];
        detection.GetTlbr(box);
        var dx = (box[0] + box[2]) / 2f - m[0];
        var dy = (box[1] + box[3]) / 2f - m[1];
        return dx * m[4] + dy * m[5] >= 0f;
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
