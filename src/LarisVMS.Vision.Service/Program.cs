using LarisVMS.Core.Dtos;
using LarisVMS.Vision.Service;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<VisionServiceOptions>(builder.Configuration.GetSection("Vision"));
builder.Services.AddHttpClient(nameof(CameraDetectionPipeline));
builder.Services.AddSingleton<CameraPipelineManager>();

// Loopback-only — this is a localhost control channel between LarisVMS.Node and this sibling
// process on the same machine (decision 3), never reached from the LAN. Port read directly from
// configuration here (not through the DI-resolved VisionServiceOptions) since Kestrel's own port
// binding has to happen before the host is built, ahead of where the options system would
// otherwise be available.
var port = builder.Configuration.GetValue("Vision:Port", 5990);
builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port));

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/cameras/{cameraId:guid}/start", async (Guid cameraId, VisionStartCameraRequest request, CameraPipelineManager manager) =>
{
    if (cameraId != request.CameraId) return Results.BadRequest(new { error = "cameraId route value must match the request body's CameraId." });

    await manager.StartOrReplaceAsync(request);
    return Results.Ok();
});

app.MapPost("/cameras/{cameraId:guid}/stop", async (Guid cameraId, CameraPipelineManager manager) =>
{
    var stopped = await manager.StopAsync(cameraId);
    return stopped ? Results.Ok() : Results.NotFound();
});

// Node's own /live/{cameraId}/detections WS relay (decision 6) polls this at the same 5-10Hz tick
// rate it forwards to a browser at — a plain GET rather than a push/WS from this side keeps the
// control channel entirely stateless and request/response, no persistent connection to manage.
app.MapGet("/cameras/{cameraId:guid}/detections", (Guid cameraId, CameraPipelineManager manager) =>
{
    var snapshot = manager.GetLiveDetections(cameraId);
    return snapshot is not null ? Results.Ok(snapshot) : Results.NotFound();
});

app.Run();
