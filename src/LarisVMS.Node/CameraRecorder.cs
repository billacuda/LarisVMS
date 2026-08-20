using LarisVMS.Media;

namespace LarisVMS.Node;

/// <summary>Bookkeeping for one camera's active recording — the CancellationTokenSource lets the
/// reconcile loop stop a camera's recorder the moment it's unassigned or disabled, without waiting
/// for the whole node to shut down. RtspUri (Main stream, credentials already injected) is carried
/// alongside the session so an on-demand snapshot grab (M8's zone editor, M5's snapshot backlog
/// item) can open its own short-lived RTSP session without RecordingSession needing to expose the
/// URI it's already using internally. PrivacyMaskSignature (M18) is what lets Reconcile detect a
/// Privacy zone being added/edited/removed on an already-recording camera and restart its session to
/// pick up the change — same shape as CameraMotionRecorder.ZoneConfigSignature.</summary>
public sealed record CameraRecorder(CancellationTokenSource Cts, Task RunTask, RecordingSession Session, string RtspUri, string PrivacyMaskSignature);
