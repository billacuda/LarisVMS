namespace LarisVMS.Core.Entities;

/// <summary>
/// Who viewed/exported/changed what — a compliance requirement in most video-surveillance
/// jurisdictions. Populated from M1 onward (login/logout, setup, settings changes); camera views,
/// PTZ moves, and exports are added as those features land in later milestones.
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
}
