using System.Security.Claims;
using LarisVMS.Core.Auth;
using LarisVMS.Core.Interfaces;

namespace LarisVMS.Web.Middleware;

/// <summary>
/// Authenticates the M20 REST API (/api/v1/*) via an "X-Api-Key" header (or HTTP Basic with the user
/// name "apikey" and the key as the password, for programs that can't set a header) — a distinct header from
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

    // Invalid keys from one address within a minute before it gets 429 instead of a lookup.
    private const int MaxInvalidPerMinute = 20;

    public async Task InvokeAsync(HttpContext context, IApiKeyService apiKeyService, ILogger<ApiKeyAuthMiddleware> logger)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(context);
            return;
        }

        var presented = context.Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrEmpty(header)
            ? header.ToString()
            : TryReadBasicKey(context.Request.Headers.Authorization.ToString());
        if (presented is not null)
        {
            var result = await apiKeyService.AuthenticateAsync(presented, context.RequestAborted);
            if (result is { } auth)
            {
                // The name is what audit entries and "created by" fields show. Still no NameIdentifier:
                // that's what tells a key from a user. See ApiKeyScope.ActorId for the ID equivalent.
                var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "API key: " + auth.KeyName),
                    new Claim(ClaimTypes.Role, auth.RoleName),
                    new Claim(ApiKeyScope.KeyIdClaim, auth.ApiKeyId.ToString()),
                    .. ApiKeyScope.ClaimsFor(auth.Limits)
                ], authenticationType: "ApiKey");
                context.User = new ClaimsPrincipal(identity);
            }
            else if (LarisVMS.Web.Services.AuthFailureLog.Record(logger, "API key", context.Connection.RemoteIpAddress) > MaxInvalidPerMinute)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = "60";
                return;
            }
        }

        await next(context);
    }

    /// <summary>The user name programs that can only send a user name and password (Home Assistant's
    /// Generic Camera, VLC) use, with the key as the password.</summary>
    public const string BasicUserName = "apikey";

    /// <summary>The key from an "Authorization: Basic" header whose user name is
    /// <see cref="BasicUserName"/>; null for anything else, so other Basic credentials are left alone.</summary>
    internal static string? TryReadBasicKey(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization) || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        string decoded;
        try { decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim())); }
        catch (FormatException) { return null; }
        var colon = decoded.IndexOf(':');
        if (colon < 0 || !string.Equals(decoded[..colon], BasicUserName, StringComparison.OrdinalIgnoreCase)) return null;
        var key = decoded[(colon + 1)..];
        return key.Length == 0 ? null : key;
    }
}
