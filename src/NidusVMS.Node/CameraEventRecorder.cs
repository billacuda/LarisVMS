namespace NidusVMS.Node;

public sealed record CameraEventRecorder(CancellationTokenSource Cts, Task RunTask, CameraEventSession Session);
