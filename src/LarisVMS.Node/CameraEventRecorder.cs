namespace LarisVMS.Node;

/// <summary>RuleConfigSignature (M8 pass 8) is compared the same way CameraMotionRecorder.
/// ZoneConfigSignature is — a changed rule set (added/edited/deleted/reordered EventTagRule) restarts
/// the session so CameraEventSession's per-rule hysteresis dictionary is rebuilt from the new set,
/// rather than the rule change silently never taking effect until the camera happens to be
/// reassigned.</summary>
public sealed record CameraEventRecorder(CancellationTokenSource Cts, Task RunTask, CameraEventSession Session, string RuleConfigSignature);
