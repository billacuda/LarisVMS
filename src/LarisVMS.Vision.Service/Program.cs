using LarisVMS.Core.Dtos;
using LarisVMS.Core.Logging;
using LarisVMS.Vision.Inference;
using LarisVMS.Vision.Models;
using LarisVMS.Vision.Service;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<VisionServiceOptions>(builder.Configuration.GetSection("Vision"));
builder.Services.AddHttpClient(nameof(CameraDetectionPipeline));
// Detection.Backend = "ExternalHttp" traffic (one POST per frame, per camera, at up to MaxFps) gets
// its own named client rather than sharing nameof(CameraDetectionPipeline)'s connection pool with
// the Node callbacks (POST /detections, /detections/crop) — a 5s-timeout detect request piling up
// against a slow external service used to compete for connections with report traffic that has
// nothing to do with it. PooledConnectionLifetime keeps connections cycling (a long-lived detect
// connection shouldn't outlive a DNS change on the far side); the default idle timeout is fine at
// this request rate.
builder.Services.AddHttpClient(CameraPipelineManager.ExternalInferenceHttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
builder.Services.AddSingleton<CameraPipelineManager>();

// This process has never had its own log file — its console output is captured by
// VisionServiceSupervisor.DrainOutputAsync and re-logged into Node's own logger, but always at
// Debug severity regardless of the original level, and Node's own FileLoggerProvider is configured
// at an Information minimum — so every line, including this pass's own high-res re-detection
// diagnostics, was silently dropped rather than ever reaching any log file. Same shared
// FileLoggerProvider Node itself uses (LarisVMS.Core.Logging — deliberately in Core so neither tier
// needs a reference the other doesn't already have), writing into the same shared logs directory
// under its own "vision-" prefix so StorageManager's existing 14-day sweep can retain it too (see
// that sweep's own updated glob).
// Initial level from the env var the node's VisionServiceSupervisor passes (the deployment-wide
// Logging.Level setting); the node also pushes changes to POST /log-level below without a restart.
var visionFileLogger = new FileLoggerProvider(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "logs"),
    "vision", LogLevels.Parse(builder.Configuration["Vision:LogLevel"]));
builder.Logging.AddProvider(visionFileLogger);

// Framework request-pipeline noise — the HttpClient play-by-play (four lines per Node callback) and
// Kestrel/routing/result "Request starting / Executing endpoint / Setting status code / Request
// finished" (six lines per /cameras/{id}/detections poll, several per second per camera), plus the
// Hosting.Lifetime startup banner. All drowned the vision log. Hidden while the app's configured
// level is Information or higher; drop it to Debug/Trace to get them back.
var frameworkLogFilter = FrameworkLogFilter.HiddenUnlessDebug(visionFileLogger);
builder.Logging.AddFilter("System.Net.Http.HttpClient", frameworkLogFilter);
builder.Logging.AddFilter("Microsoft.AspNetCore", frameworkLogFilter);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", frameworkLogFilter);

// Loopback-only — this is a localhost control channel between LarisVMS.Node and this sibling
// process on the same machine (decision 3), never reached from the LAN. Port read directly from
// configuration here (not through the DI-resolved VisionServiceOptions) since Kestrel's own port
// binding has to happen before the host is built, ahead of where the options system would
// otherwise be available.
var port = builder.Configuration.GetValue("Vision:Port", 5990);
builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port));

var app = builder.Build();

// Resolve the ONNX Runtime backend for this machine's hardware once, before any camera pipeline
// builds an InferenceSession. Every node package ships CUDA / DirectML / OpenVINO / CPU native
// runtimes side by side (build-node.ps1's StageOnnxBackends); this points the OS loader at one of
// them based on the accelerator the node passed in Vision__PreferredAccelerator, degrading down the
// list when a backend's prerequisites (CUDA Toolkit, cuDNN) are missing. The chosen backend and any
// operator action needed are logged here and reported back to the control plane on the heartbeat.
VisionBackendResolver.Initialize(key => builder.Configuration[key], app.Logger);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// The backend VisionBackendResolver settled on plus anything a site admin needs to do about it —
// polled once by NodeWorker after the process starts and forwarded on the node heartbeat.
app.MapGet("/backend", () => Results.Ok(VisionBackendResolver.Current));

// Node pushes the deployment-wide Logging.Level here when it changes — takes effect immediately, no
// restart. Loopback-only like every other route on this port.
app.MapPost("/log-level", (LogLevelRequest request) =>
{
    visionFileLogger.MinLevel = LogLevels.Parse(request.Level);
    return Results.NoContent();
});

app.MapPost("/cameras/{cameraId:guid}/start", async (Guid cameraId, VisionStartCameraRequest request,
    CameraPipelineManager manager, ILogger<Program> logger) =>
{
    if (cameraId != request.CameraId) return Results.BadRequest(new { error = "cameraId route value must match the request body's CameraId." });

    // Caught and echoed back rather than left to bubble into a bare 500: starting a pipeline builds
    // the YoloEngine, which is where a machine-level setup problem actually surfaces (a missing CUDA
    // runtime DLL, an unreadable/absent .onnx model, a GPU the driver won't hand out). Without this,
    // Node can only log "returned InternalServerError" and the real reason is buried in this
    // process's own logging — confirmed the hard way by a node missing cublasLt64_12.dll, where the
    // one line that explained it only existed in the Windows Application event log.
    try
    {
        await manager.StartOrReplaceAsync(request);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to start the detection pipeline for camera {CameraId}.", cameraId);
        return Results.Problem(detail: Describe(ex), statusCode: StatusCodes.Status500InternalServerError,
            title: "Failed to start the detection pipeline.");
    }
});

// The cameras this process is actually watching. NodeWorker's reconcile compares its own _activeVision
// against this and re-issues a start for anything missing — the only way it can now learn that a
// pipeline's engine build failed, since that happens after /start has already returned 200.
app.MapGet("/cameras", (CameraPipelineManager manager) => Results.Ok(manager.WatchedCameraIds()));

// Richer per-camera state for the Dashboard's "still starting AI detection" spinner — NodeWorker
// polls this and folds it into each camera's StreamInfoReportItem. Deliberately separate from /cameras
// above, which must keep returning a bare Guid list.
app.MapGet("/cameras/status", (CameraPipelineManager manager) => Results.Ok(manager.GetCameraStatuses()));

// The model-agnostic dropdown's data source — every .onnx ModelDiscovery finds in this node's models
// directory, with whatever descriptor/decoder it resolved (or a warning if it didn't). Node's own
// GET /vision/models proxies this for Admin's Detection.LocalModelName picker.
app.MapGet("/models", (CameraPipelineManager manager) => Results.Ok(manager.ListDiscoveredModels()
    .Select(m => new DiscoveredModelDto(m.Name, m.Descriptor?.Decoder.ToString(), m.Descriptor?.InputSize, m.Source.ToString(), m.Warnings))
    .ToList()));

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

/// <summary>Flattens an exception chain to its messages only — no stack trace. What makes these
/// failures diagnosable is almost always the innermost message (ONNX Runtime's own "which depends on
/// X.dll which is missing" text, for instance), and this crosses a process boundary into Node's log,
/// where a full stack trace per reconcile tick would be noise rather than signal.</summary>
static string Describe(Exception ex)
{
    var messages = new List<string>();
    for (Exception? current = ex; current is not null; current = current.InnerException)
    {
        if (!messages.Contains(current.Message)) messages.Add(current.Message);
    }
    return string.Join(" -> ", messages);
}
