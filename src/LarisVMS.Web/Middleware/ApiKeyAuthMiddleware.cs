using System.Security.Claims;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Authenticates the M20 REST API (/api/v1/*) via an "X-Api-Key" header — a distinct header from
/// NodeAuthMiddleware's "Authorization: Bearer {nodeId}:{secret}", which is a differently-shaped
/// credential for a different caller (recorder nodes). Registered right after NodeAuthMiddleware, same
/// placement between UseAuthentication and UseAuthorization.
///
/// Unlike NodeAuthMiddleware (which stashes its result on HttpContext.Items because those routes never
/// use [Authorize]), this sets HttpContext.User to a real ClaimsPrincipal — carrying only a
/// ClaimTypes.Role claim (the key's bound role's name), no ClaimTypes.NameIdentifier, since an API key
/// is a role-bound credential, not a disguised user. PermissionAuthorizationHandler and
/// CameraAccessService both know how to resolve permissions from a role-only, user-less principal (see
/// their own doc comments) — that's what lets a key ride the existing [Authorize("Resource.Action")]
/// pipeline unmodified.
///
/// A missing or invalid key is deliberately not rejected here with a hard 401 (unlike
/// NodeAuthMiddleware, whose routes have no legitimate anonymous/cookie path at all) — this leaves
/// whatever UseAuthentication() already put on HttpContext.User untouched, so a signed-in admin's own
/// cookie session can also reach /api/v1/* in a browser, and RequireAuthorization on each endpoint
/// produces the correct 401/403 either way.
/// </summary>
public class ApiKeyAuthMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Api-Key";

    public async Task InvokeAsync(HttpContext context, IApiKeyService apiKeyService)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(context);
            return;
        }

        if (context.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrEmpty(header))
        {
            var result = await apiKeyService.AuthenticateAsync(header.ToString(), context.RequestAborted);
            if (result is { } auth)
            {
                var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Role, auth.RoleName),
                    new Claim("ApiKeyId", auth.ApiKeyId.ToString())
                ], authenticationType: "ApiKey");
                context.User = new ClaimsPrincipal(identity);
            }
        }

        await next(context);
    }
}
