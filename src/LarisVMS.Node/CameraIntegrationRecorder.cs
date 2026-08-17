namespace LarisVMS.Node;

/// <summary>One running vendor-plugin session, tracked exactly like CameraEventRecorder tracks an
/// ONVIF one. IntegrationKey is kept alongside so a reconcile can tell "already running the right
/// plugin" from "the camera's detected integration changed and this session must be replaced" —
/// which happens for real when a re-probe reports a different make/model, or a version adds a
/// provider that now recognizes hardware already in the fleet.</summary>
public sealed record CameraIntegrationRecorder(
    CancellationTokenSource Cts, Task RunTask, DahuaCgiEventSession Session, string IntegrationKey);
