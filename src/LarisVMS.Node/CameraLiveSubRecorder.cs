using LarisVMS.Media;

namespace LarisVMS.Node;

/// <summary>Bookkeeping for one camera's always-on Sub live session (M18: adaptive streaming) — same
/// shape as CameraMotionRecorder, with StreamSignature standing in for ZoneConfigSignature: the Sub
/// stream's RtspUri (credentials injected) is the only thing that can change out from under an
/// already-running session between reconciles (a camera's Sub stream address edited, or its
/// credentials rotated), so that's what Reconcile compares to decide "leave it running" vs
/// "restart".</summary>
public sealed record CameraLiveSubRecorder(CancellationTokenSource Cts, Task RunTask, SubLiveSession Session, string StreamSignature);
