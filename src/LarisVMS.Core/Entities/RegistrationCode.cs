namespace LarisVMS.Core.Entities;

/// <summary>
/// A one-time registration code: registers a single node or proxy, then stops working, and expires
/// if unused. An alternative to the shared registration key, which every install can reuse until
/// it's changed. Only a hash of the code is stored; the code is shown once when it's created.
/// </summary>
public class RegistrationCode
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 (hex) of the code.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public string? CreatedBy { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }

    /// <summary>When it was used; also the concurrency check that stops two registrations sharing it.</summary>
    public DateTime? UsedUtc { get; set; }

    /// <summary>Host name of the node or proxy that used it.</summary>
    public string? UsedBy { get; set; }
}
