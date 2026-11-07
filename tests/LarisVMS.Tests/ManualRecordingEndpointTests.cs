using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>Builds the real endpoints. A handler parameter that can't bind only fails when endpoints
/// are built, which is the first request in production, so this catches it here instead.</summary>
public class ManualRecordingEndpointTests
{
    private static WebApplication NewApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IManualRecordingService>(_ => null!);
        builder.Services.AddScoped<ICameraAccessService>(_ => null!);
        builder.Services.AddScoped<IAuditService>(_ => null!);
        builder.Services.AddScoped<ApplicationDbContext>(_ => null!);
        builder.Services.AddSingleton<NodeControlClient>(_ => null!);
        return builder.Build();
    }

    private static List<RouteEndpoint> Endpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();

    private static List<string?> Policies(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();

    [Fact]
    public void EveryRecordingRouteBindsAndNamesAPermission()
    {
        var app = NewApp();
        app.MapManualRecordingApi();

        var endpoints = Endpoints(app);

        Assert.Equal(5, endpoints.Count);
        Assert.All(endpoints, e => Assert.True(e.RoutePattern.RawText!.StartsWith("/api/v1/"), e.RoutePattern.RawText));
        Assert.All(endpoints.Where(e => e.RoutePattern.RawText!.EndsWith("/start") || e.RoutePattern.RawText!.EndsWith("/stop")),
            e => Assert.Contains("Recording.Manual", Policies(e)));
        Assert.Contains("Cameras.View", Policies(endpoints.Single(e => e.RoutePattern.RawText == "/api/v1/recording")));
    }

    [Fact]
    public void ASharedRouteAppliesItsConventionsToEveryCopy()
    {
        var app = NewApp();
        var routes = new ApiRoutes(app.MapGroup("/api"), app.MapGroup("/api/v1"));

        routes.Get("/things", () => Results.Ok()).RequireAuthorization("Things.View");

        var endpoints = Endpoints(app);
        Assert.Equal(["/api/things", "/api/v1/things"], endpoints.Select(e => e.RoutePattern.RawText).Order());
        Assert.All(endpoints, e => Assert.Contains("Things.View", Policies(e)));
    }

    [Fact]
    public void ASharedRouteAppliesACameraAccessCheckToEveryCopy()
    {
        var app = NewApp();
        var routes = new ApiRoutes(app.MapGroup("/api"), app.MapGroup("/api/v1"));

        routes.Get("/cameras/{cameraId:guid}/things", (Guid cameraId) => Results.Ok()).RequireCameraAccess(CameraAccessActions.Playback);

        Assert.Equal(2, Endpoints(app).Count);
    }
}
