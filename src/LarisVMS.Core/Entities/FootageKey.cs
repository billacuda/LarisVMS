namespace LarisVMS.Core.Entities;

/// <summary>
/// The footage master key: wraps every per-camera, per-day data key (FootageKey). Stored encrypted
/// with the server's Data Protection keys — which are tied to this machine, so the passphrase-protected
/// backup (FootageKeyService.ExportBackupAsync) is what survives rebuilding the server. Losing both
/// makes every encrypted recording unreadable for good; encryption can't be turned on until a backup
/// has been downloaded.
/// </summary>
public class FootageMasterKey
{
    public Guid Id { get; set; }

    /// <summary>32 random bytes, base64; encrypted at rest by the column converter.</summary>
    public string Key { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When a backup of this key was last downloaded; null = never (encryption stays off).</summary>
    public DateTime? BackupDownloadedAt { get; set; }
}

/// <summary>One camera's data key for one UTC day — what its footage files from that day are sealed
/// with (FootageCrypto). Wrapped with AES-GCM under the master key; a node asks for it by camera and
/// day when encrypting and by id when reading, and keeps it in memory only.</summary>
public class FootageKey
{
    public Guid Id { get; set; }
    public Guid CameraId { get; set; }
    public DateOnly Day { get; set; }
    public Guid MasterKeyId { get; set; }

    /// <summary>base64(nonce ‖ ciphertext ‖ tag); associated data = key id ‖ camera id.</summary>
    public string Wrapped { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
