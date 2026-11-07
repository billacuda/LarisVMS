using LarisVMS.Core.Dtos;
using LarisVMS.Core.Enums;
using LarisVMS.Media;

namespace LarisVMS.Node;

/// <summary>
/// M8 pass 3, generalized in the Schedule/Event pass: a Motion- or Event-mode segment awaiting its
/// keep/discard decision, deferred until <see cref="DecideAtUtc"/>. The decision can't be made the
/// instant the segment completes because a motion event (or, for Event mode, a driving
/// EventTagRule) that starts shortly *after* the segment ends should still be able to retroactively
/// claim it as pre-roll — that's only knowable once PreRoll seconds have actually elapsed with no
/// event un-claimed. See MotionSession.HasMotionSince's doc comment for how the eventual comparison
/// (evaluated at DecideAtUtc) covers both pre-roll and post-roll from one check.
///
/// Schedule and Manual modes have nothing to claim retroactively, so they arrive with
/// <see cref="DiscardReason"/> already set. They still wait, because every discard waits out
/// NodeWorker.ManualStartGrace in case a manual recording covering the segment hasn't reached this
/// node yet. Motion and Event segments pick up a reason the same way once their own check says discard.
/// </summary>
public sealed record PendingGatedSegmentDecision(
    Guid CameraId, string CameraName, RecordingSegment Segment, NodeConfigStreamDto? MainStream,
    RecordingMode Mode, int PostRollSeconds, DateTime DecideAtUtc, string? DiscardReason = null);
