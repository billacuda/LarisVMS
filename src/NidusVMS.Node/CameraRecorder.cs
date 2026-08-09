using NidusVMS.Media;

namespace NidusVMS.Node;

/// <summary>Bookkeeping for one camera's active recording — the CancellationTokenSource lets the
/// reconcile loop stop a camera's recorder the moment it's unassigned or disabled, without waiting
/// for the whole node to shut down.</summary>
public sealed record CameraRecorder(CancellationTokenSource Cts, Task RunTask, RecordingSession Session);
