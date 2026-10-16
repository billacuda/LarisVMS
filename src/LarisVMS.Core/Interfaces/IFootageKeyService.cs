namespace LarisVMS.Core.Interfaces;

public record FootageKeyStatus(bool HasMasterKey, Guid? MasterKeyId, DateTime? CreatedAt, DateTime? BackupDownloadedAt)
{
    /// <summary>Encryption can be used: there's a master key and a backup of it has been downloaded.</summary>
    public bool Ready => HasMasterKey && BackupDownloadedAt is not null;
}

public record EncryptionBackupRequest(string? Passphrase);
public record EncryptionRestoreRequest(string? Backup, string? Passphrase);

public enum FootageKeyRestoreResult { Restored, AlreadyInstalled, WrongPassphrase, NotABackup, DifferentKeyInstalled }

/// <summary>Footage encryption keys: the master key (create, back up, restore) and the per-camera,
/// per-day data keys nodes encrypt and decrypt with.</summary>
public interface IFootageKeyService
{
    Task<FootageKeyStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Creates the master key if there isn't one. Returns the status either way.</summary>
    Task<FootageKeyStatus> CreateMasterKeyAsync(CancellationToken ct = default);

    /// <summary>The master key sealed with a passphrase, as a JSON file's text; records that a backup
    /// was made. Null when there's no master key.</summary>
    Task<string?> ExportBackupAsync(string passphrase, CancellationToken ct = default);

    Task<FootageKeyRestoreResult> RestoreBackupAsync(string backupJson, string passphrase, CancellationToken ct = default);

    /// <summary>The camera's data key for a UTC day, created on first use. Null when encryption isn't
    /// ready (no master key, or no backup yet).</summary>
    Task<(Guid KeyId, byte[] Key)?> GetOrCreateDataKeyAsync(Guid cameraId, DateOnly day, CancellationToken ct = default);

    /// <summary>A data key by id, with its camera — for decrypting. Null when unknown.</summary>
    Task<(Guid CameraId, byte[] Key)?> GetDataKeyAsync(Guid keyId, CancellationToken ct = default);
}
