using Microsoft.AspNetCore.Authorization;

namespace LarisVMS.Core.Auth;

public class PermissionRequirement(string resource, string action) : IAuthorizationRequirement
{
    public string Resource { get; } = resource;
    public string Action { get; } = action;
}
