// Ported from ByteTrack — https://github.com/FoundationVision/ByteTrack
// SPDX-License-Identifier: MIT
// Copyright (c) 2021 Yifu Zhang
//
// C# port of deploy/TensorRT/cpp/src/STrack.cpp and yolox/tracker/byte_tracker.py (STrack).
//
// Ported verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Tracking\STrack.cs).

namespace LarisVMS.Vision.Tracking;

internal enum TrackState
{
    New = 0,
    Tracked = 1,
    Lost = 2,
    Removed = 3,
}

/// <summary>
/// One tracked object: its Kalman state, its lifecycle, and the detection index it matched this
/// frame. Boxes are carried as top-left/width/height, and converted to the filter's
/// centre/aspect/height form at the boundary.
/// </summary>
internal sealed class STrack
{
    private readonly float[] _initialTlwh = new float[4];

    public STrack(ReadOnlySpan<float> tlwh, float score, int classId)
    {
        tlwh.CopyTo(_initialTlwh);
        Tlwh = new float[4];
        tlwh.CopyTo(Tlwh);

        Score = score;
        ClassId = classId;
        State = TrackState.New;
        IsActivated = false;
        TrackId = 0;
        TrackletLength = 0;
    }

    public float[] Tlwh { get; private set; }
    public float Score { get; private set; }
    public int ClassId { get; private set; }
    public int TrackId { get; private set; }
    public TrackState State { get; private set; }
    public bool IsActivated { get; private set; }
    public int FrameId { get; private set; }
    public int StartFrame { get; private set; }
    public int TrackletLength { get; private set; }

    /// <summary>Index into the current frame's detection list, or -1. Used to write IDs back.</summary>
    public int DetectionIndex { get; set; } = -1;

    public float[]? Mean { get; private set; }
    public float[,]? Covariance { get; private set; }

    /// <summary>Converts top-left/width/height to the filter's [cx, cy, aspect, height].</summary>
    public static void TlwhToXyah(ReadOnlySpan<float> tlwh, Span<float> xyah)
    {
        xyah[0] = tlwh[0] + (tlwh[2] * 0.5f);
        xyah[1] = tlwh[1] + (tlwh[3] * 0.5f);
        xyah[2] = tlwh[3] == 0f ? 0f : tlwh[2] / tlwh[3];
        xyah[3] = tlwh[3];
    }

    public static void TlbrToTlwh(Span<float> box)
    {
        box[2] -= box[0];
        box[3] -= box[1];
    }

    /// <summary>Top-left/bottom-right form, which is what IoU is computed on.</summary>
    public void GetTlbr(Span<float> tlbr)
    {
        tlbr[0] = Tlwh[0];
        tlbr[1] = Tlwh[1];
        tlbr[2] = Tlwh[0] + Tlwh[2];
        tlbr[3] = Tlwh[1] + Tlwh[3];
    }

    /// <summary>Refreshes <see cref="Tlwh"/> from the Kalman mean, unless the track is brand new.</summary>
    private void RefreshTlwh()
    {
        if (State == TrackState.New || Mean is null)
        {
            Array.Copy(_initialTlwh, Tlwh, 4);
            return;
        }

        // mean is [cx, cy, aspect, height]; width is aspect * height.
        var height = Mean[3];
        var width = Mean[2] * height;

        Tlwh[0] = Mean[0] - (width * 0.5f);
        Tlwh[1] = Mean[1] - (height * 0.5f);
        Tlwh[2] = width;
        Tlwh[3] = height;
    }

    public void Activate(ByteKalmanFilter filter, int frameId, int trackId)
    {
        TrackId = trackId;

        Span<float> xyah = stackalloc float[4];
        TlwhToXyah(_initialTlwh, xyah);

        var (mean, covariance) = filter.Initiate(xyah);
        Mean = mean;
        Covariance = covariance;

        TrackletLength = 0;
        State = TrackState.Tracked;

        // A track born on the very first frame has nothing to be corroborated against, so it is
        // trusted immediately. Later ones must survive a second frame before being reported.
        if (frameId == 1)
        {
            IsActivated = true;
        }

        FrameId = frameId;
        StartFrame = frameId;

        RefreshTlwh();
    }

    public void ReActivate(ByteKalmanFilter filter, STrack detection, int frameId, int? newTrackId)
    {
        Span<float> xyah = stackalloc float[4];
        TlwhToXyah(detection.Tlwh, xyah);

        filter.Update(Mean!, Covariance!, xyah);

        TrackletLength = 0;
        State = TrackState.Tracked;
        IsActivated = true;
        FrameId = frameId;
        Score = detection.Score;
        ClassId = detection.ClassId;

        if (newTrackId is { } id)
        {
            TrackId = id;
        }

        RefreshTlwh();
    }

    public void Update(ByteKalmanFilter filter, STrack detection, int frameId)
    {
        FrameId = frameId;
        TrackletLength++;

        Span<float> xyah = stackalloc float[4];
        TlwhToXyah(detection.Tlwh, xyah);

        filter.Update(Mean!, Covariance!, xyah);

        State = TrackState.Tracked;
        IsActivated = true;
        Score = detection.Score;
        ClassId = detection.ClassId;

        RefreshTlwh();
    }

    public void MarkLost() => State = TrackState.Lost;

    public void MarkRemoved() => State = TrackState.Removed;

    /// <summary>
    /// Advances every track's filter one frame. A track that is not currently matched has its
    /// height velocity zeroed first, which stops lost tracks from inflating or collapsing while
    /// they coast.
    /// </summary>
    public static void MultiPredict(IEnumerable<STrack> tracks, ByteKalmanFilter filter)
    {
        foreach (var track in tracks)
        {
            if (track.Mean is null || track.Covariance is null)
            {
                continue;
            }

            if (track.State != TrackState.Tracked)
            {
                track.Mean[7] = 0f;
            }

            filter.Predict(track.Mean, track.Covariance);
            track.RefreshTlwh();
        }
    }
}
