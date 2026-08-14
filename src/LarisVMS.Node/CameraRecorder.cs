using LarisVMS.Media;

namespace LarisVMS.Node;

/// <summary>Bookkeeping for one camera's active recording — the CancellationTokenSource lets the
/// reconcile loop stop a camera's recorder the moment it's unassigned or disabled, without waiting
/// for the whole node to shut down. RtspUri (Main stream, credentials already injected) is carried
/// alongside the session so an on-demand snapshot grab (M8's zone editor, M5's snapshot backlog
/// item) can open its own short-lived RTSP session without RecordingSession needing to expose the
/// URI it's already using internally.</summary>
public sealed record CameraRecorder(CancellationTokenSource Cts, Task RunTask, RecordingSession Session, string RtspUri);
