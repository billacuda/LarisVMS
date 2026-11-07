namespace LarisVMS.Core.Entities;

/// <summary>One permission a limited API key keeps. Only read when
/// <see cref="ApiKey.PermissionsLimited"/> is set; see <see cref="Auth.ApiKeyScope"/>.</summary>
public class ApiKeyPermission
{
    public Guid ApiKeyId { get; set; }
    public string Resource { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}
