using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NidusVMS.Core.Security;
using NidusVMS.Media;
using NidusVMS.Node;

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
    var response = await registerClient.RegisterAsync(
        new NidusVMS.Core.Dtos.NodeRegisterRequest(registrationKey, Environment.MachineName, NodeVersion.Current, Environment.OSVersion.Platform.ToString()),
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

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(livePort));
builder.Services.AddWindowsService(o => o.ServiceName = "NidusVMS Node");
builder.Services.AddSingleton(apiClient);
builder.Services.AddSingleton(sp => new NodeWorker(
    apiClient, ffmpegPath, fallbackStorageRoot, livePort, config.MediaSigningKey, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<NodeWorker>());
builder.Services.AddSingleton<IHostedService>(sp => new StorageManager(
    apiClient, fallbackStorageRoot, sp.GetRequiredService<ILoggerFactory>().CreateLogger<StorageManager>()));

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

await app.RunAsync();

static string? GetArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
