using NidusVMS.Core.Dtos;
using NidusVMS.Media;

namespace NidusVMS.Node;

/// <summary>
/// M8 pass 3: a Motion-mode segment awaiting its keep/discard decision, deferred until
/// <see cref="DecideAtUtc"/>. The decision can't be made the instant the segment completes because
/// a motion event that starts shortly *after* the segment ends should still be able to retroactively
/// claim it as pre-roll — that's only knowable once PreRoll seconds have actually elapsed with no
/// motion event un-claimed. See MotionSession.HasMotionSince's doc comment for how the eventual
/// comparison (evaluated at DecideAtUtc) covers both pre-roll and post-roll from one check.
/// </summary>
public sealed record PendingMotionSegmentDecision(
    Guid CameraId, string CameraName, RecordingSegment Segment, NodeConfigStreamDto? MainStream,
    int PostRollSeconds, DateTime DecideAtUtc);
