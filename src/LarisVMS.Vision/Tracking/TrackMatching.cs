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
