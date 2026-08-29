using LarisVMS.Media;

namespace LarisVMS.Node;

/// <summary>Bookkeeping for one camera's active motion session — same shape as CameraRecorder, plus
/// ZoneConfigSignature so Reconcile can cheaply tell "zone set is unchanged, leave this running" from
/// "a zone was added/edited/removed, this session's masks are stale and must be rebuilt" without a
/// live-swap API on MotionSession itself (restart is simpler and zone edits are rare compared to the
/// 30s reconcile cadence). Despite the name, ReconcileMotion now folds the node's resolved hwaccel
/// value into this same string too (pass 0 of the detection/hardware-acceleration overhaul) — one
/// signature, not two, since both changes need the identical "restart this session" response.</summary>
public sealed record CameraMotionRecorder(CancellationTokenSource Cts, Task RunTask, MotionSession Session, string ZoneConfigSignature);
