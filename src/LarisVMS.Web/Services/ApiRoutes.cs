using LarisVMS.Core.Enums;

namespace LarisVMS.Web.Services;

/// <summary>
/// Maps one handler under several route roots. The public API uses this to offer a route on both its
/// existing path, which the web UI calls, and under /api/v1, the only place API keys are accepted.
/// One handler means the two can't drift apart, and no other route starts accepting keys.
/// </summary>
public sealed class ApiRoutes(params IEndpointRouteBuilder[] roots)
{
    public ApiRouteBuilder Get(string path, Delegate handler) => new(roots.Select(r => r.MapGet(path, handler)).ToList());
    public ApiRouteBuilder Post(string path, Delegate handler) => new(roots.Select(r => r.MapPost(path, handler)).ToList());
    public ApiRouteBuilder Delete(string path, Delegate handler) => new(roots.Select(r => r.MapDelete(path, handler)).ToList());
}

/// <summary>Applies conventions such as RequireAuthorization to every copy of a route.</summary>
public sealed class ApiRouteBuilder(IReadOnlyList<RouteHandlerBuilder> builders) : IEndpointConventionBuilder
{
    public void Add(Action<EndpointBuilder> convention)
    {
        foreach (var builder in builders) builder.Add(convention);
    }

    public void Finally(Action<EndpointBuilder> finallyConvention)
    {
        foreach (var builder in builders) builder.Finally(finallyConvention);
    }

    public ApiRouteBuilder RequireCameraAccess(CameraAccessActions action)
    {
        foreach (var builder in builders) builder.RequireCameraAccess(action);
        return this;
    }
}
