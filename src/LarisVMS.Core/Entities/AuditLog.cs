namespace LarisVMS.Core.Entities;

/// <summary>
/// Who viewed/exported/changed what — a compliance requirement in most video-surveillance
/// jurisdictions. Populated from M1 onward (login/logout, setup, settings changes); camera views,
/// PTZ moves, and exports are added as those features land in later milestones.
///
/// <see cref="Hash"/> and <see cref="PreviousHash"/> chain every entry to the one before it (see
/// <c>AuditChain</c>), so an entry changed, removed, or slipped in afterwards shows up when the log
/// is checked. Entries written before the chain existed have neither.
/// </summary>
public class AuditLog
{
    public long Id { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? IpAddress { get; set; }
    public string? Details { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>The <see cref="Hash"/> of the entry before this one ("" for the first).</summary>
    public string? PreviousHash { get; set; }

    /// <summary>HMAC-SHA256 (hex) over <see cref="PreviousHash"/> and this entry's fields.</summary>
    public string? Hash { get; set; }
}
