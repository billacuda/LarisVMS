using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using LarisVMS.Core.Auth;

namespace LarisVMS.Infrastructure.Auth;

/// <summary>
/// Resolves any policy name shaped "{Resource}.{Action}" (e.g. "Cameras.Edit") into a
/// PermissionRequirement-based policy on the fly, rather than requiring every resource × action
/// combination in the RBAC matrix to be individually registered with AddPolicy in Program.cs.
/// Explicitly registered policies (e.g. "AdministratorOnly") still take precedence via the fallback
/// default provider.
/// </summary>
public class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback = new(options);

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var existing = await _fallback.GetPolicyAsync(policyName);
        if (existing != null) return existing;

        var parts = policyName.Split('.', 2);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;

        return new AuthorizationPolicyBuilder()
            .AddRequirements(new PermissionRequirement(parts[0], parts[1]))
            .Build();
    }
}
