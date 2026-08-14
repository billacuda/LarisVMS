using LarisVMS.Core.Enums;

namespace LarisVMS.Core.Entities;

/// <summary>
/// One "export this timeframe for these cameras" request — a single submission from the Playback
/// page's Export form, fanning out to one ExportJobItem per camera (the actual per-camera unit of
/// work a recorder node executes; "one output file per camera" is exactly what that split gives us).
///
/// Shared/global across every user holding Exports.View, not scoped to whoever created it — same
/// convention as a shared View (IsShared=true), except here there is no private variant at all: see
/// RequestedByUserId's own doc comment.
/// </summary>
public class ExportJob
{
    public Guid Id { get; set; }

    /// <summary>ApplicationUser.Id of whoever clicked Export — auditing only, never used to filter
    /// what a viewer sees (every user with Exports.View sees every job). Matches the type
    /// AuditLog.UserId and View.OwnerId already use for the same IdentityUser string key.</summary>
    public string RequestedByUserId { get; set; } = string.Empty;

    /// <summary>Denormalized alongside RequestedByUserId the same way AuditLog carries both UserId
    /// and UserName — the Exports page needs something human-readable to show without a lookup back
    /// to Identity, and a user's display name can change after the fact, so this is a snapshot of
    /// what it was at creation time, not a live join.</summary>
    public string? RequestedByUserName { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }

    /// <summary>Rollup of Items' own Status — see ExportJobStatus's doc comment for the exact rule.
    /// Recomputed by ExportService any time an item's status changes, not stored independently of
    /// them.</summary>
    public ExportJobStatus Status { get; set; } = ExportJobStatus.Queued;

    public List<ExportJobItem> Items { get; set; } = [];
}
