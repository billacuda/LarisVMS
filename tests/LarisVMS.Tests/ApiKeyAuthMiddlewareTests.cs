using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using LarisVMS.Core.Interfaces;
using LarisVMS.Web.Middleware;

namespace LarisVMS.Tests;

public class ApiKeyAuthMiddlewareTests
{
    private static readonly Guid ValidKeyId = Guid.NewGuid();

    /// <summary>Recognizes exactly one raw value ("good-key") as valid, bound to "API/Integration" —
    /// enough to exercise the middleware's own header-parsing and principal-building without a real
    /// database (IApiKeyService.AuthenticateAsync's own hashing/lookup logic is ApiKeyServiceTests'
    /// concern, not this middleware's).</summary>
    private sealed class FakeApiKeyService : IApiKeyService
    {
        public Task<List<Core.Entities.ApiKey>> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(Core.Entities.ApiKey Key, string RawValue)> GenerateAsync(string name, string roleId, string? createdByUserId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RevokeAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();

        public Task<(Guid ApiKeyId, string RoleName)?> AuthenticateAsync(string rawValue, CancellationToken ct = default)
            => Task.FromResult(rawValue == "good-key" ? (ValidKeyId, "API/Integration") : ((Guid, string)?)null);
    }

    // InvokeAsync's IApiKeyService parameter is called directly here rather than resolved through DI —
    // ASP.NET Core's own middleware-invoker does that resolution at request time in the real pipeline
    // (from context.RequestServices), but a unit test calling InvokeAsync itself just passes it in.
    private static async Task<HttpContext> Run(string path, string? headerValue)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (headerValue is not null) context.Request.Headers["X-Api-Key"] = headerValue;

        var middleware = new ApiKeyAuthMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, new FakeApiKeyService());
        return context;
    }

    [Fact]
    public async Task AValidKeyOnTheApiV1PrefixSetsARoleOnlyAuthenticatedPrincipal()
    {
        var context = await Run("/api/v1/status", "good-key");

        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.Equal("API/Integration", context.User.FindFirstValue(ClaimTypes.Role));
        Assert.Null(context.User.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(ValidKeyId.ToString(), context.User.FindFirstValue("ApiKeyId"));
    }

    [Fact]
    public async Task AnInvalidKeyLeavesThePrincipalUnauthenticated()
    {
        var context = await Run("/api/v1/status", "wrong-key");

        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task AMissingHeaderLeavesThePrincipalUnauthenticated()
    {
        var context = await Run("/api/v1/status", null);

        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
    }

    [Fact]
    public async Task AValidKeyOutsideTheApiV1PrefixIsIgnored()
    {
        // A valid key presented against some other route shouldn't authenticate anything — this
        // middleware only ever looks at /api/v1/*, everything else is somebody else's concern.
        var context = await Run("/api/dashboard", "good-key");

        Assert.False(context.User.Identity?.IsAuthenticated ?? false);
    }
}
