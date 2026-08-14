using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Security;
using NidusVMS.Media;
using NidusVMS.Node;
using NidusVMS.Node.Update;
using NidusVMS.Onvif.Clients;
using NidusVMS.Onvif.Soap;

// ── First-run registration ──────────────────────────────────────────────────
// If node.config doesn't exist yet, this run must be given --server-url and --registration-key
// (what install-node.ps1 passes) to register once; the assigned NodeId/secret are then persisted
// and every subsequent run just loads them.
var config = NodeConfigStore.Load();
if (config is null)
{
    var serverUrl = GetArg(args, "--server-url") ?? Environment.GetEnvironmentVariable("NIDUSVMS_SERVER_URL");
    var registrationKey = GetArg(args, "--registration-key") ?? Environment.GetEnvironmentVariable("NIDUSVMS_REGISTRATION_KEY");
    if (serverUrl is null || registrationKey is null)
    {
        Console.Error.WriteLine("Not yet registered. Run with --server-url <url> --registration-key <key> " +
            "(or set NIDUSVMS_SERVER_URL / NIDUSVMS_REGISTRATION_KEY) the first time.");
        Environment.Exit(1);
        return;
    }

    var insecure = HasFlag(args, "--insecure-tls") || Environment.GetEnvironmentVariable("NIDUSVMS_INSECURE_TLS") == "1";
    var registerClient = new NodeApiClient(serverUrl, insecure);
    // "win-x64" (a RID, not Environment.OSVersion.Platform.ToString()'s "Win32NT") — this is what
    // NidusVMS.Web's heartbeat handler matches against NodeBuildVersion.Platform to decide whether an
    // uploaded build applies to this node (see NodeVersionComparer/Program.cs's auto-update check).
    // NidusVMS.Node is Windows-only and single-RID for now (see NodeConfigStore's doc comment on why
    // there's no Linux build), so this is a fixed literal rather than something resolved at runtime.
    var response = await registerClient.RegisterAsync(
        new NidusVMS.Core.Dtos.NodeRegisterRequest(registrationKey, Environment.MachineName, NodeVersion.Current, "win-x64"),
        CancellationToken.None);

    config = new NodeConfig(serverUrl, response.NodeId, response.Secret, response.MediaSigningKey);
    NodeConfigStore.Save(config);
    Console.WriteLine($"Registered as node {response.NodeId}.");
}

var insecureTls = HasFlag(args, "--insecure-tls") || Environment.GetEnvironmentVariable("NIDUSVMS_INSECURE_TLS") == "1";
var apiClient = new NodeApiClient(config.ServerUrl, insecureTls);
apiClient.SetCredentials(config.NodeId, config.Secret);

var ffmpegPath = FfmpegPathResolver.Resolve(
    GetArg(args, "--ffmpeg-path") ?? Environment.GetEnvironmentVariable("NIDUSVMS_FFMPEG_PATH"));
if (!await FfmpegPathResolver.IsRunnableAsync(ffmpegPath))
{
    Console.Error.WriteLine($"ffmpeg could not be run at '{ffmpegPath}'. Set --ffmpeg-path or NIDUSVMS_FFMPEG_PATH, " +
        "or install ffmpeg and ensure it's on PATH.");
    Environment.Exit(1);
    return;
}

var fallbackStorageRoot = GetArg(args, "--storage-root") ?? Environment.GetEnvironmentVariable("NIDUSVMS_STORAGE_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NidusVMS", "recordings");

// M5 live view: this node's own Kestrel port, reached only by NidusVMS.Web proxying a browser's live
// view request — plain HTTP, never TLS, and never dialed directly by a browser. See the plan's
// "Media path" section for why: every node needing its own cert (self-signed and asking each viewer
// to trust it, or a real one via an internal CA) was rejected in favor of NidusVMS.Web always proxying,
// which needs nothing installed on the node at all. LastIpAddress (M4, captured server-side from
// this node's own outbound connections) plus this port is the address NidusVMS.Web dials.
var livePort = int.TryParse(GetArg(args, "--live-port") ?? Environment.GetEnvironmentVariable("NIDUSVMS_LIVE_PORT"), out var parsedPort)
    ? parsedPort : 8554;

// M8 pass 6: a separate HttpClient from NodeApiClient's own (15s, for short REST control-plane
// calls) — PullMessagesAsync is a deliberate long-poll that holds the connection open for up to
// CameraEventSession's own 30s Timeout value while waiting for the camera to have something to
// report, so this needs real headroom past that, not NodeApiClient's budget. Same cert-ignoring
// rationale as NidusVMS.Web's "onvif" named client: LAN cameras almost universally present a
// self-signed certificate with no CA behind it.
var onvifHttpClient = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
})
{ Timeout = TimeSpan.FromSeconds(45) };
var onvifEventsClient = new OnvifEventsClient(new OnvifSoapClient(onvifHttpClient));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(livePort));
builder.Services.AddWindowsService(o => o.ServiceName = "NidusVMS Node");
builder.Services.AddSingleton(apiClient);
builder.Services.AddSingleton(sp => new UpdateService(
    config, insecureTls, sp.GetRequiredService<ILoggerFactory>().CreateLogger<UpdateService>(),
    sp.GetRequiredService<IHostApplicationLifetime>()));
builder.Services.AddSingleton(sp => new NodeWorker(
    apiClient, ffmpegPath, fallbackStorageRoot, livePort, config, sp.GetRequiredService<ILoggerFactory>(), onvifEventsClient,
    sp.GetRequiredService<UpdateService>()));
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<NodeWorker>());
builder.Services.AddSingleton<IHostedService>(sp => new StorageManager(
    apiClient, fallbackStorageRoot, sp.GetRequiredService<ILoggerFactory>().CreateLogger<StorageManager>()));
builder.Services.AddSingleton(sp => new ExportRunner(
    apiClient, ffmpegPath, sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExportRunner>()));

var app = builder.Build();
app.UseWebSockets();

var liveLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LiveView");

app.Map("/live/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, NodeWorker worker) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Read at request time, not a value captured once at startup — worker.MediaSigningKey is kept
    // current by every reconcile cycle (see NodeWorker's doc comment), specifically so a node that
    // had none locally at startup (registered before M5) still validates correctly within a
    // reconcile cycle or two, no restart needed.
    var token = ctx.Request.Query["token"].ToString();
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (!MediaToken.TryValidate(token, cameraId, currentKey, out var tokenError))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }

    var session = worker.TryGetSession(cameraId);
    if (session is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("Camera is not currently recording on this node.");
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await LiveViewerHandler.RunAsync(socket, session, liveLogger, ctx.RequestAborted);
});

// M7 playback: serves exactly one segment file's raw bytes to NidusVMS.Web's proxy — never reached
// by a browser directly, same "IIS proxies every byte" shape as /live above. The token binds
// cameraId + this exact path, so path itself can't be tampered with independently of the
// signature; the directory-prefix check below is a second, independent line of defense in case
// that ever changes, not a substitute for it.
app.MapGet("/playback-segment/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, NodeWorker worker) =>
{
    var token = ctx.Request.Query["token"].ToString();
    var path = ctx.Request.Query["path"].ToString();
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (string.IsNullOrEmpty(path))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("missing path");
        return;
    }
    if (!MediaToken.TryValidateSegment(token, cameraId, path, currentKey, out var tokenError))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }

    var storageRoot = worker.StorageRoot;
    if (storageRoot is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }

    string fullPath, cameraDir;
    try
    {
        fullPath = Path.GetFullPath(path);
        cameraDir = Path.GetFullPath(Path.Combine(storageRoot, $"cam-{cameraId}", "main")) + Path.DirectorySeparatorChar;
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!fullPath.StartsWith(cameraDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ctx.Response.ContentType = "video/mp4";
    await ctx.Response.SendFileAsync(fullPath, ctx.RequestAborted);
});

// Export trigger — POSTed by NidusVMS.Web's ExportJobDispatcher, never reached by a browser
// directly. Token binds cameraId + exportItemId (MediaToken.IssueForExport/TryValidateExport)
// rather than a specific file the way /playback-segment's does, since the whole point of this call
// is *telling* the node which files to concat — there's nothing to bind ahead of time. The
// path-prefix check below is applied to every entry in the request body, exactly as strict as
// /playback-segment's own check, so a compromised/rogue Web tier can't point this at a path outside
// this camera's own recording directory. Responds 202 immediately (after validating and staging
// everything ffmpeg needs) and runs the actual concat on a background Task.Run.
app.MapPost("/export/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, ExportRequest request, NodeWorker worker, ExportRunner exportRunner) =>
{
    var token = ctx.Request.Query["token"].ToString();
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (request.CameraId != cameraId)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("cameraId mismatch");
        return;
    }
    if (!MediaToken.TryValidateExport(token, cameraId, request.ExportItemId, currentKey, out var tokenError))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }

    var storageRoot = worker.StorageRoot;
    if (storageRoot is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }

    // Validated defensively even though ExportJobDispatcher already sanitizes this before sending
    // it — this value ends up in Path.Combine(exportsDir, outputFileName) below, and a compromised/
    // rogue Web tier passing "../../whatever" must not be able to escape exportsDir.
    var outputFileName = request.OutputFileName;
    if (string.IsNullOrWhiteSpace(outputFileName)
        || outputFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        || outputFileName.Contains(".."))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("invalid output file name");
        return;
    }

    string cameraDir;
    var validatedPaths = new List<string>();
    try
    {
        cameraDir = Path.GetFullPath(Path.Combine(storageRoot, $"cam-{cameraId}", "main")) + Path.DirectorySeparatorChar;
        foreach (var p in request.SegmentFilePaths)
        {
            var fullPath = Path.GetFullPath(p);
            if (!fullPath.StartsWith(cameraDir, StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("segment path outside this camera's recording directory");
                return;
            }
            validatedPaths.Add(fullPath);
        }
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (validatedPaths.Count == 0)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("no segment paths given");
        return;
    }

    ctx.Response.StatusCode = StatusCodes.Status202Accepted;
    await ctx.Response.CompleteAsync();

    // Fire-and-forget: the request is already acknowledged above. CancellationToken.None, not
    // ctx.RequestAborted — this must keep running (and eventually report back) even though the HTTP
    // request that started it has already completed.
    _ = Task.Run(() => exportRunner.RunAsync(request.ExportItemId, cameraId, validatedPaths, storageRoot, outputFileName, CancellationToken.None));
});

// Export download side — serves exactly one finished export item's file to NidusVMS.Web's
// /export-download proxy, same "IIS/this proxy relays every byte, browser never reached directly"
// shape as /playback-segment, using the export-download token family (binds exportItemId + this
// exact path) rather than the export-trigger family above.
app.MapGet("/export-file/{exportItemId:guid}", async (HttpContext ctx, Guid exportItemId, NodeWorker worker) =>
{
    var token = ctx.Request.Query["token"].ToString();
    var path = ctx.Request.Query["path"].ToString();
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (string.IsNullOrEmpty(path))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("missing path");
        return;
    }
    if (!MediaToken.TryValidateExportDownload(token, exportItemId, path, currentKey, out var tokenError))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }

    var storageRoot = worker.StorageRoot;
    if (storageRoot is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }

    string fullPath, exportsDir;
    try
    {
        fullPath = Path.GetFullPath(path);
        exportsDir = Path.GetFullPath(Path.Combine(storageRoot, "exports")) + Path.DirectorySeparatorChar;
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!fullPath.StartsWith(exportsDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    ctx.Response.ContentType = "video/mp4";
    await ctx.Response.SendFileAsync(fullPath, ctx.RequestAborted);
});

// M8/M5: one-shot still frame, e.g. the zone editor's background image. Same token family as /live
// (Issue/TryValidate) rather than /playback-segment's — this authorizes a viewer for this camera's
// media in general, same as live view, not one specific file.
app.MapGet("/snapshot/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, NodeWorker worker) =>
{
    var token = ctx.Request.Query["token"].ToString();
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (!MediaToken.TryValidate(token, cameraId, currentKey, out var tokenError))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }

    var bytes = await worker.CaptureSnapshotAsync(cameraId, ctx.RequestAborted);
    if (bytes is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync("Could not capture a frame from this camera (unreachable, not currently recording on this node, or timed out).");
        return;
    }

    ctx.Response.ContentType = "image/jpeg";
    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
});

await app.RunAsync();

static string? GetArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
