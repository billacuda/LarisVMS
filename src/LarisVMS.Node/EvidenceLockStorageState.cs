namespace LarisVMS.Node;

/// <summary>Shared between StorageManager (which sets it each sweep) and NodeWorker (which reports it
/// in the heartbeat): the primary volume is over its watermark and evidence-locked footage is what
/// keeps it there, since locked footage is never deleted.</summary>
public static class EvidenceLockStorageState
{
    private static volatile bool _blocked;

    public static bool BlockedByEvidenceLocks
    {
        get => _blocked;
        set => _blocked = value;
    }
}
