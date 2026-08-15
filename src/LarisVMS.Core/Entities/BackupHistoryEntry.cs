using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// M11: one BACKUP DATABASE run — SQL Express installs (this app's typical deployment) have no SQL
/// Agent to schedule backups with, so the app is the scheduler. Ported from rsolva's own
/// BackupHistoryEntry/BackupService (near-verbatim; see BackupService's own doc comment for what
/// changed). Restore is deliberately out of scope for this pass — an admin restores by other means
/// (SSMS, sqlcmd) against the .bak files this produces.
/// </summary>
public class BackupHistoryEntry
{
    public int Id { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public BackupStatus Status { get; set; }
    /// <summary>"manual" or "scheduled".</summary>
    public string TriggeredBy { get; set; } = "manual";
    public string? FilePath { get; set; }
    public long? SizeBytes { get; set; }
    public string? Error { get; set; }
}
