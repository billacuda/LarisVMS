namespace LarisVMS.Node;

/// <summary>One running vendor-plugin session, tracked exactly like CameraEventRecorder tracks an
/// ONVIF one. IntegrationKey is kept alongside so a reconcile can tell "already running the right
/// plugin" from "the camera's detected integration changed and this session must be replaced" —
/// which happens for real when a re-probe reports a different make/model, or a version adds a
/// provider that now recognizes hardware already in the fleet.
///
/// BaseUri is compared for the same reason and was originally missing, which was a real bug: the
/// address is derived from the camera's DeviceServiceUri (see NodeService.ResolveIntegrationBaseUri),
/// so turning HTTPS off on a camera changes it from https://host to http://host. With only the key
/// compared, the running session kept its now-dead https address forever and logged an endless
/// "connection dropped — will reconnect" loop; nothing short of restarting the node picked up the new
/// address. Confirmed live on two cameras whose HTTPS was switched off.</summary>
public sealed record CameraIntegrationRecorder(
    CancellationTokenSource Cts, Task RunTask, DahuaCgiEventSession Session, string IntegrationKey, string BaseUri);
