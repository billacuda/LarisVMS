using Microsoft.AspNetCore.Identity;

namespace LarisVMS.Core.Entities;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when AD group sync (not an admin) disabled this account — sync only re-enables
    /// accounts it disabled itself, so a manual disable sticks.</summary>
    public bool DisabledByDirectorySync { get; set; }
}
