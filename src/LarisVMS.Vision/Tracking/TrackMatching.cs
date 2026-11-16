// Ported from ByteTrack — https://github.com/FoundationVision/ByteTrack
// SPDX-License-Identifier: MIT
// Copyright (c) 2021 Yifu Zhang
//
// C# port of yolox/tracker/matching.py and the ious/iou_distance/linear_assignment helpers in
// deploy/TensorRT/cpp/src/BYTETracker.cpp.
//
// Ported verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Tracking\TrackMatching.cs).

using YoloDotNet.Trackers;

namespace LarisVMS.Vision.Tracking;

internal static class TrackMatching
{
    /// <summary>
    /// Cost matrix of 1 - IoU between predicted track boxes and detection boxes.
    /// Lower cost means a better match.
    /// </summary>
    public static float[,] IouDistance(IReadOnlyList<STrack> tracks, IReadOnlyList<STrack> detections)
    {
        var cost = new float[tracks.Count, detections.Count];

        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];

        for (var i = 0; i < tracks.Count; i++)
        {
            tracks[i].GetTlbr(a);

            for (var j = 0; j < detections.Count; j++)
            {
                detections[j].GetTlbr(b);
                cost[i, j] = 1f - Iou(a, b);
            }
        }

        return cost;
    }

    /// <summary>
    /// 1 - IoU with both boxes enlarged by <paramref name="buffer"/> times their own width and height
    /// on every side (buffered IoU, as in C-BIoU). For an object that moved further than its own size
    /// between analysed frames — a car when detection runs at a few frames a second — the plain boxes
    /// don't overlap at all; the enlarged ones still do. Only a same-class pair of similar size (area
    /// within 2x) can match.
    /// </summary>
    public static float[,] BufferedIouDistance(IReadOnlyList<STrack> tracks, IReadOnlyList<STrack> detections, float buffer)
    {
        var cost = new float[tracks.Count, detections.Count];

        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];

        for (var i = 0; i < tracks.Count; i++)
        {
            tracks[i].GetTlbr(a);
            var areaA = Math.Max(0f, a[2] - a[0]) * Math.Max(0f, a[3] - a[1]);
            Expand(a, buffer);

            for (var j = 0; j < detections.Count; j++)
            {
                detections[j].GetTlbr(b);
                var areaB = Math.Max(0f, b[2] - b[0]) * Math.Max(0f, b[3] - b[1]);
                if (tracks[i].ClassId != detections[j].ClassId || areaA <= 0f || areaB <= 0f
                    || areaA > 2f * areaB || areaB > 2f * areaA)
                {
                    cost[i, j] = 1f;
                    continue;
                }
                Expand(b, buffer);
                cost[i, j] = 1f - Iou(a, b);
            }
        }

        return cost;
    }

    /// <summary>
    /// 1 - IoU between where each track was last actually seen (<see cref="STrack.LastSeenTlwh"/>,
    /// not its prediction) and each detection, same class only. Used to hand a parked object's box
    /// back to its own track when whatever covered it moves on.
    /// </summary>
    public static float[,] LastSeenIouDistance(IReadOnlyList<STrack> tracks, IReadOnlyList<STrack> detections)
    {
        var cost = new float[tracks.Count, detections.Count];

        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];

        for (var i = 0; i < tracks.Count; i++)
        {
            var last = tracks[i].LastSeenTlwh;
            a[0] = last[0];
            a[1] = last[1];
            a[2] = last[0] + last[2];
            a[3] = last[1] + last[3];

            for (var j = 0; j < detections.Count; j++)
            {
                if (tracks[i].ClassId != detections[j].ClassId)
                {
                    cost[i, j] = 1f;
                    continue;
                }
                detections[j].GetTlbr(b);
                cost[i, j] = 1f - Iou(a, b);
            }
        }

        return cost;
    }

    /// <summary>
    /// Rules out pairing a track with a detection of a clearly different shape — the side of one car
    /// against the back of another — by setting that pair's cost to 1 (never a match). See
    /// <see cref="ShapeDiffers"/>; <paramref name="exempt"/> lets the caller excuse a pair.
    /// </summary>
    public static void ApplyShapeGate(float[,] cost, IReadOnlyList<STrack> tracks, IReadOnlyList<STrack> detections,
        float maxChange, Func<STrack, STrack, bool> exempt)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            for (var j = 0; j < detections.Count; j++)
            {
                if (ShapeDiffers(tracks[i], detections[j], maxChange) && !exempt(tracks[i], detections[j]))
                {
                    cost[i, j] = 1f;
                }
            }
        }
    }

    /// <summary>
    /// Whether a detection's shape (width / height) is further than <paramref name="maxChange"/> from
    /// the track's usual shape. False when the track's shape isn't settled yet, the detection is cut
    /// off at the frame edge, or the check is turned off (<paramref name="maxChange"/> of 1 or less).
    /// </summary>
    public static bool ShapeDiffers(STrack track, STrack detection, float maxChange)
    {
        if (maxChange <= 1f || track.AspectSamples < MinShapeSamples || track.RefAspect <= 0f
            || detection.IsClipped || detection.Tlwh[2] <= 0f || detection.Tlwh[3] <= 0f)
        {
            return false;
        }

        var aspect = detection.Tlwh[2] / detection.Tlwh[3];
        return Math.Max(aspect, track.RefAspect) / Math.Min(aspect, track.RefAspect) > maxChange;
    }

    /// <summary>Share of the detection's area that lies inside the box the track was last seen in.</summary>
    public static float ShareInsideLastSeen(STrack track, STrack detection)
    {
        var last = track.LastSeenTlwh;
        var box = detection.Tlwh;
        var area = box[2] * box[3];
        if (area <= 0f)
        {
            return 0f;
        }

        var width = Math.Min(box[0] + box[2], last[0] + last[2]) - Math.Max(box[0], last[0]);
        var height = Math.Min(box[1] + box[3], last[1] + last[3]) - Math.Max(box[1], last[1]);
        return width <= 0f || height <= 0f ? 0f : width * height / area;
    }

    /// <summary>Sightings needed before a track's shape is trusted for <see cref="ApplyShapeGate"/>.</summary>
    public const int MinShapeSamples = 3;

    private static void Expand(Span<float> tlbr, float buffer)
    {
        var dx = (tlbr[2] - tlbr[0]) * buffer;
        var dy = (tlbr[3] - tlbr[1]) * buffer;
        tlbr[0] -= dx;
        tlbr[1] -= dy;
        tlbr[2] += dx;
        tlbr[3] += dy;
    }

    private static float Iou(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var left = Math.Max(a[0], b[0]);
        var top = Math.Max(a[1], b[1]);
        var right = Math.Min(a[2], b[2]);
        var bottom = Math.Min(a[3], b[3]);

        var width = right - left;
        var height = bottom - top;

        if (width <= 0f || height <= 0f)
        {
            return 0f;
        }

        var intersection = width * height;
        var areaA = Math.Max(0f, a[2] - a[0]) * Math.Max(0f, a[3] - a[1]);
        var areaB = Math.Max(0f, b[2] - b[0]) * Math.Max(0f, b[3] - b[1]);
        var union = areaA + areaB - intersection;

        return union <= 0f ? 0f : intersection / union;
    }

    /// <summary>
    /// Folds detection confidence into the IoU cost, so a geometrically plausible match to a
    /// weak detection loses to an equally plausible match against a strong one.
    /// Mirrors matching.fuse_score, which the reference applies on every non-MOT20 run.
    /// </summary>
    public static void FuseScore(float[,] cost, IReadOnlyList<STrack> detections)
    {
        var rows = cost.GetLength(0);
        var columns = cost.GetLength(1);

        for (var i = 0; i < rows; i++)
        {
            for (var j = 0; j < columns; j++)
            {
                var iouSimilarity = 1f - cost[i, j];
                cost[i, j] = 1f - (iouSimilarity * detections[j].Score);
            }
        }
    }

    /// <summary>
    /// Solves the assignment and discards any pairing whose cost exceeds <paramref name="threshold"/>.
    ///
    /// The reference ships its own 342-line lapjv.cpp. YoloDotNet already exposes an equivalent
    /// Jonker-Volgenant solver as public API, which pads rectangular matrices and returns -1 for
    /// unassigned rows, so this delegates to it and applies the threshold filter separately --
    /// exactly what the reference's linear_assignment does around its own solver.
    /// </summary>
    public static (List<(int Track, int Detection)> Matches, List<int> UnmatchedTracks, List<int> UnmatchedDetections)
        LinearAssignment(float[,] cost, int trackCount, int detectionCount, float threshold)
    {
        var matches = new List<(int, int)>();
        var unmatchedTracks = new List<int>();
        var unmatchedDetections = new List<int>();

        if (trackCount == 0 || detectionCount == 0)
        {
            unmatchedTracks.AddRange(Enumerable.Range(0, trackCount));
            unmatchedDetections.AddRange(Enumerable.Range(0, detectionCount));
            return (matches, unmatchedTracks, unmatchedDetections);
        }

        var assignment = LAPJV.Solve(cost);
        var detectionMatched = new bool[detectionCount];

        for (var track = 0; track < trackCount; track++)
        {
            var detection = track < assignment.Length ? assignment[track] : -1;

            // Padding columns from the rectangular solve fall outside the real detection range.
            if (detection >= 0 && detection < detectionCount && cost[track, detection] <= threshold)
            {
                matches.Add((track, detection));
                detectionMatched[detection] = true;
            }
            else
            {
                unmatchedTracks.Add(track);
            }
        }

        for (var detection = 0; detection < detectionCount; detection++)
        {
            if (!detectionMatched[detection])
            {
                unmatchedDetections.Add(detection);
            }
        }

        return (matches, unmatchedTracks, unmatchedDetections);
    }
}
