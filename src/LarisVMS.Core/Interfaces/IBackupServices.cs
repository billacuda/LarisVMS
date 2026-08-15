using LarisVMS.Core.Entities;

namespace LarisVMS.Core.Interfaces;

/// <summary>M11: runs and records BACKUP DATABASE against this app's own database. Ported from
/// rsolva's IBackupService/BackupService — see BackupService's own doc comment.</summary>
public interface IBackupService
{
    Task<BackupHistoryEntry> RunBackupAsync(string triggeredBy, CancellationToken ct = default);
}
