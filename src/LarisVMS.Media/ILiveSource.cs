namespace LarisVMS.Media;

/// <summary>
/// What LiveViewerHandler needs from a live-fMP4-producing session — extracted so it can serve a
/// viewer from either RecordingSession's own live tee leg (Main) or SubLiveSession (M18: adaptive
/// streaming) without knowing which. RecordingSession already exposes exactly this shape (its
/// existing WaitForLiveInitSegmentAsync/LiveFragmentReceived members satisfy the interface with no
/// changes of their own beyond declaring it) — this is a pure extraction, not a behavior change.
/// </summary>
public interface ILiveSource
{
    /// <summary>Waits for the current ffmpeg attempt's live init segment (ftyp+moov). Throws
    /// OperationCanceledException if ct fires (typically a caller-supplied timeout) before one
    /// arrives, or if this attempt ends (crash, stop, restart) before producing one.</summary>
    Task<byte[]> WaitForLiveInitSegmentAsync(CancellationToken ct);

    /// <summary>Raised once per complete fMP4 fragment (moof+mdat) after the init segment is
    /// available. Each array is a fresh copy safe to hold onto past the event call.</summary>
    event Action<byte[]>? LiveFragmentReceived;
}
