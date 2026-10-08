namespace LarisVMS.Web.Services;

/// <summary>A newer LarisVMS release than the one running: its version (e.g. "0.215.0") and its
/// GitHub release page.</summary>
public sealed record AvailableRelease(string Version, string Url);

/// <summary>
/// What <see cref="ReleaseCheckService"/> last found, read by both layouts to show the top-bar
/// "new version" notice. Singleton, same shape as ClientEndpointCspCache: a volatile reference the
/// page renders read without locking.
/// </summary>
public sealed class ReleaseCheckState
{
    private volatile AvailableRelease? _available;
    private TaskCompletionSource _checkRequested = NewSignal();

    /// <summary>The newer release, or null when up to date, the check is off, or it hasn't run yet.</summary>
    public AvailableRelease? Available => _available;

    public void Set(AvailableRelease? available) => _available = available;

    /// <summary>Asks the background service to check now rather than at its next daily run — used
    /// when the setting is switched back on.</summary>
    public void RequestCheck() => _checkRequested.TrySetResult();

    internal Task WaitForCheckRequestAsync() => _checkRequested.Task;

    internal void ResetCheckRequest()
    {
        if (_checkRequested.Task.IsCompleted) _checkRequested = NewSignal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
