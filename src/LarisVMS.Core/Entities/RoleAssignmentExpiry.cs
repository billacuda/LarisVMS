namespace LarisVMS.Core.Entities;

/// <summary>
/// A single (user, role) grant's own expiry — the source of truth for whether that specific
/// assignment expires, independent of RoleProfile.AutoExpires: an admin can always override one
/// assignment to be permanent or time-limited regardless of the role's own default. A row's absence
/// means that assignment never expires. Schema-only in the pass that introduces this table — the
/// sweep that actually acts on expired rows, and the Users page UI to set/view them, land in a later
/// pass once RoleProfile's expiry defaults exist to seed from.
/// </summary>
public class RoleAssignmentExpiry
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
    public string? AssignedByUserId { get; set; }
}
