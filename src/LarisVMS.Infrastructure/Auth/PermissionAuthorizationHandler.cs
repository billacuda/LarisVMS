using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Infrastructure.Auth;

public class PermissionAuthorizationHandler(IPermissionService permissionService)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is not null)
        {
            if (await permissionService.HasPermissionAsync(userId, requirement.Resource, requirement.Action))
                context.Succeed(requirement);
            return;
        }

        // No user identity — an API-key-authenticated principal (M20), bound to a Role and nothing
        // else (see ApiKeyAuthMiddleware). Resolve straight from the role claim instead of a user id.
        var roleName = context.User.FindFirstValue(ClaimTypes.Role);
        if (roleName is not null &&
            await permissionService.HasPermissionForRoleNameAsync(roleName, requirement.Resource, requirement.Action))
        {
            context.Succeed(requirement);
        }
    }
}
