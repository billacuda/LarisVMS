namespace LarisVMS.Core.Entities;

/// <summary>
/// Links an Active Directory group to a LarisVMS role. Members of the group (including nested
/// members) get the role on each sync. Linked by SID, not name, so renaming the group in AD doesn't
/// break the link — GroupName is only a display copy that sync refreshes.
/// </summary>
public class AdGroupRoleLink
{
    public Guid Id { get; set; }
    public string GroupSid { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;

    /// <summary>Set by sync when the SID no longer resolves in AD (group deleted). A missing group
    /// contributes no members.</summary>
    public bool IsMissing { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
}
