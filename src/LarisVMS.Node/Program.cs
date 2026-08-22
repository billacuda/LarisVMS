using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Logging;
using LarisVMS.Core.Security;
using LarisVMS.Media;
using LarisVMS.Node;
using LarisVMS.Node.Update;
using LarisVMS.Onvif.Clients;
using LarisVMS.Onvif.Soap;

// M18 follow-up: below this, /playback-segment's partial-fetch path (see its own route below) isn't
// worth the extra request round trip + fragment-index lookup — a whole-file fetch that starts within
// the first couple seconds is already about as fast as a partial one would be, and this fleet's real
// segments have a fragment every ~1s anyway (see the CHANGELOG entry that introduced this), so
// there'd be nothing meaningful to skip regardless.
const double MinSeekSecondsForPartialFetch = 2.0;

// ── First-run registration ──────────────────────────────────────────────────
// If node.config doesn't exist yet, this run must be given --server-url and --registration-key
// (what install-node.ps1 passes) to register once; the assigned NodeId/secret are then persisted
// and every subsequent run just loads them.
var config = NodeConfigStore.Load();
if (config is null)
{
    var serverUrl = GetArg(args, "--server-url") ?? Environment.GetEnvironmentVariable("LARISVMS_SERVER_URL");
    var registrationKey = GetArg(args, "--registration-key") ?? Environment.GetEnvironmentVariable("LARISVMS_REGISTRATION_KEY");
    if (serverUrl is null || registrationKey is null)
    {
        Console.Error.WriteLine("Not yet registered. Run with --server-url <url> --registration-key <key> " +
            "(or set LARISVMS_SERVER_URL / LARISVMS_REGISTRATION_KEY) the first time.");
        Environment.Exit(1);
        return;
    }

    var insecure = HasFlag(args, "--insecure-tls") || Environment.GetEnvironmentVariable("LARISVMS_INSECURE_TLS") == "1";
    var registerClient = new NodeApiClient(serverUrl, insecure);
    // "win-x64" (a RID, not Environment.OSVersion.Platform.ToString()'s "Win32NT") — this is what
    // LarisVMS.Web's heartbeat handler matches against NodeBuildVersion.Platform to decide whether an
    // uploaded build applies to this node (see NodeVersionComparer/Program.cs's auto-update check).
    // LarisVMS.Node is Windows-only and single-RID for now (see NodeConfigStore's doc comment on why
    // there's no Linux build), so this is a fixed literal rather than something resolved at runtime.
    var response = await registerClient.RegisterAsync(
        new LarisVMS.Core.Dtos.NodeRegisterRequest(registrationKey, Environment.MachineName, NodeVersion.Current, "win-x64"),
        CancellationToken.None);

    config = new NodeConfig(serverUrl, response.NodeId, response.Secret, response.MediaSigningKey);
    NodeConfigStore.Save(config);
    Console.WriteLine($"Registered as node {response.NodeId}.");
}

var insecureTls = HasFlag(args, "--insecure-tls") || Environment.GetEnvironmentVariable("LARISVMS_INSECURE_TLS") == "1";
var apiClient = new NodeApiClient(config.ServerUrl, insecureTls);
apiClient.SetCredentials(config.NodeId, config.Secret);

var ffmpegPath = FfmpegPathResolver.Resolve(
    GetArg(args, "--ffmpeg-path") ?? Environment.GetEnvironmentVariable("LARISVMS_FFMPEG_PATH"));
if (!await FfmpegPathResolver.IsRunnableAsync(ffmpegPath))
{
    Console.Error.WriteLine($"ffmpeg could not be run at '{ffmpegPath}'. Set --ffmpeg-path or LARISVMS_FFMPEG_PATH, " +
        "or install ffmpeg and ensure it's on PATH.");
    Environment.Exit(1);
    return;
}

var fallbackStorageRoot = GetArg(args, "--storage-root") ?? Environment.GetEnvironmentVariable("LARISVMS_STORAGE_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "recordings");

// M5 live view: this node's own Kestrel port, reached only by LarisVMS.Web proxying a browser's live
// view request — plain HTTP, never TLS, and never dialed directly by a browser. See the plan's
// "Media path" section for why: every node needing its own cert (self-signed and asking each viewer
// to trust it, or a real one via an internal CA) was rejected in favor of LarisVMS.Web always proxying,
// which needs nothing installed on the node at all. LastIpAddress (M4, captured server-side from
// this node's own outbound connections) plus this port is the address LarisVMS.Web dials.
var livePort = int.TryParse(GetArg(args, "--live-port") ?? Environment.GetEnvironmentVariable("LARISVMS_LIVE_PORT"), out var parsedPort)
    ? parsedPort : 8554;

// M8 pass 6: a separate HttpClient from NodeApiClient's own (15s, for short REST control-plane
// calls) — PullMessagesAsync is a deliberate long-poll that holds the connection open for up to
// CameraEventSession's own 30s Timeout value while waiting for the camera to have something to
// report, so this needs real headroom past that, not NodeApiClient's budget. Same cert-ignoring
// rationale as LarisVMS.Web's "onvif" named client: LAN cameras almost universally present a
// self-signed certificate with no CA behind it.
var onvifHttpClient = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
})
{ Timeout = TimeSpan.FromSeconds(45) };
var onvifEventsClient = new OnvifEventsClient(new OnvifSoapClient(onvifHttpClient));

var builder = WebApplication.CreateBuilder(args);
// M11: a Windows Service has no console anyone will ever see — file capture is the only way to
// diagnose a node after the fact. Sibling of node.config's own %ProgramData%\LarisVMS\ (NodeConfigStore),
// already proven writable by this same service account. StorageManager.SweepLogsDirectory (fixed
// 14-day window) is this tier's retention sweep, run alongside its other periodic disk cleanup.
builder.Logging.AddProvider(new FileLoggerProvider(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "logs"),
    "node", LogLevel.Information));
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(livePort));
builder.Services.AddWindowsService(o => o.ServiceName = "LarisVMS Node");
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
builder.Services.AddSingleton<IHostedService>(sp => new ThumbnailBackfillService(
    apiClient, sp.GetRequiredService<NodeWorker>(), fallbackStorageRoot,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<ThumbnailBackfillService>()));
builder.Services.AddSingleton(sp => new ExportRunner(
    apiClient, ffmpegPath, sp.GetRequiredService<ILoggerFactory>().CreateLogger<ExportRunner>()));

var app = builder.Build();
app.UseWebSockets();

var liveLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LiveView");
var playbackLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Playback");

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

    // M18: ?role=sub asks for the adaptive-streaming Sub live session instead of Main — not part of
    // the signed token (see this route's own doc comment above `token`): the token already
    // authorizes viewing *this camera's* live feed, and which quality tier of that same feed a
    // viewer's browser asks for is a client preference, not a separate authorization boundary, the
    // same way playback's own quality choices aren't token-bound either. Falls back to Main whenever
    // Sub isn't actually available (toggle off, no Sub stream, session not up yet) rather than
    // failing the request — a tile that briefly can't get Sub should still show something.
    var wantsSub = string.Equals(ctx.Request.Query["role"].ToString(), "sub", StringComparison.OrdinalIgnoreCase);
    ILiveSource? session = wantsSub ? worker.TryGetLiveSubSession(cameraId) : null;
    session ??= worker.TryGetSession(cameraId);
    if (session is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("Camera is not currently recording on this node.");
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await LiveViewerHandler.RunAsync(socket, session, liveLogger, ctx.RequestAborted);
});

// M7 playback: serves exactly one segment file's raw bytes to LarisVMS.Web's proxy — never reached
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

    // M18 follow-up: a late seek into a large segment used to mean downloading everything before it
    // first — confirmed live against this fleet's real segment sizes (24-44MB at 60s/4K-HEVC) as the
    // actual cause of "slow to load" when scrubbing, not disk speed. seekSeconds, when present, is an
    // unsigned hint — like /live's own ?role=, not part of the token, since it only picks *where
    // inside* an already-authorized file to start from, not a new authorization scope. Below
    // MinSeekSecondsForPartialFetch, or with no usable fragment index, falls straight through to the
    // original whole-file SendFileAsync below — unconditionally correct, just potentially slower.
    if (double.TryParse(ctx.Request.Query["seekSeconds"], NumberStyles.Float, CultureInfo.InvariantCulture, out var seekSeconds)
        && seekSeconds > MinSeekSecondsForPartialFetch)
    {
        var fragments = await worker.GetOrBuildFragmentIndexAsync(fullPath, ctx.RequestAborted);
        // Mp4Fragment.MediaTimeSeconds is the raw tfdt baseMediaDecodeTime, an absolute media-timeline
        // value — but seekSeconds (from the client) is relative to the *segment's* wall-clock start.
        // These only happen to be the same number when a file's media timeline starts at zero, which
        // this fleet's recordings don't reliably do (confirmed live from the client side — see
        // playback-player.js's own X-Fragment-Start-Seconds comment). fragments[0] is the first moof
        // in the file, so its own MediaTimeSeconds is exactly the offset to subtract to make every
        // comparison below segment-relative; previously, a nonzero base here made seeks land early by
        // that amount, and a base of a minute or more made this loop break on the very first fragment
        // every time, silently disabling partial fetch for that file entirely.
        var baseMediaTimeSeconds = fragments.Count > 0 ? fragments[0].MediaTimeSeconds : 0.0;
        // The last fragment at-or-before the target — never *past* it, so the client's own "wait
        // until the target instant is actually buffered" logic (playback-player.js) still has
        // something to wait for rather than silently landing later than what was asked for.
        Mp4Fragment? chosen = null;
        foreach (var f in fragments)
        {
            if (f.MediaTimeSeconds - baseMediaTimeSeconds > seekSeconds) break;
            chosen = f;
        }

        // fragments[0] is always the init segment's own end (the first top-level box after
        // ftyp+moov is the first moof) — skipping straight to it would just be the whole file, so
        // this is only worth it when there's a genuinely *later* fragment to jump to.
        if (fragments.Count > 0 && chosen is { } target && target.ByteOffset > fragments[0].ByteOffset)
        {
            // Opened (and retried) before anything is written to the response — a transient storage
            // blip here just falls through to the whole-file path below, rather than failing the
            // request outright, since nothing has been committed to the client yet at this point.
            FileStream? fileStream = null;
            try
            {
                fileStream = await StorageRetry.ExecuteAsync(playbackLogger, $"Opening {fullPath} for partial fetch", () =>
                    Task.FromResult(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, useAsync: true)),
                    ctx.RequestAborted);
            }
            catch (IOException ex)
            {
                playbackLogger.LogWarning(ex, "Giving up on the partial fetch for {Path} after retries — falling back to a whole-file send.", fullPath);
            }

            if (fileStream is not null)
            {
                await using (fileStream)
                {
                    // Read by the client to correct its own "buffered start + seekSeconds" seek math
                    // — the served fragment's own media time, not the segment's true start, is now
                    // what sourceBuffer.buffered.start(0) will actually report once appended. Segment-
                    // relative (see baseMediaTimeSeconds above), matching seekSeconds's own units — the
                    // client only ever works in segment-relative time and has no way to know this
                    // file's absolute media-timeline base. Set only now, after the open has actually
                    // succeeded — setting it earlier and then falling back to a whole-file send below
                    // would leave a stale header on a response it doesn't describe.
                    ctx.Response.Headers["X-Fragment-Start-Seconds"] =
                        (target.MediaTimeSeconds - baseMediaTimeSeconds).ToString(CultureInfo.InvariantCulture);
                    var initSegmentEnd = fragments[0].ByteOffset;
                    await CopyExactAsync(fileStream, ctx.Response.Body, initSegmentEnd, ctx.RequestAborted);
                    fileStream.Seek(target.ByteOffset, SeekOrigin.Begin);
                    await fileStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
                }
                return;
            }
        }
    }

    // Whole-file path — probes that the file can actually be opened (retried) before committing to
    // SendFileAsync's own internal open, so a transient storage blip gets the same few-retry grace
    // here too instead of failing the request outright. The probe's own handle is closed immediately;
    // SendFileAsync opens its own.
    try
    {
        await StorageRetry.ExecuteAsync(playbackLogger, $"Opening {fullPath}", () =>
        {
            using var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(true);
        }, ctx.RequestAborted);
    }
    catch (IOException ex)
    {
        playbackLogger.LogWarning(ex, "Giving up on {Path} after retries — storage still unreachable.", fullPath);
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        return;
    }

    await ctx.Response.SendFileAsync(fullPath, ctx.RequestAborted);
});

// M7 pass 2: hover-thumbnail proxy target — same shape as /playback-segment above (token binds
// cameraId + path + offsetSeconds; directory-prefix check is the second, independent line of
// defense), but serves one extracted JPEG frame instead of a whole segment's bytes, with an
// on-disk cache checked first. The cache path is derived only from the already-validated fullPath
// (substituting the "main" segment of the path for "thumbs") and the token-bound offsetSeconds —
// never from anything else the client sends, so it can't be spoofed into naming an arbitrary file.
app.MapGet("/playback-thumbnail/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, NodeWorker worker) =>
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
    if (string.IsNullOrEmpty(path) || !int.TryParse(ctx.Request.Query["offset"], out var offsetSeconds) || offsetSeconds < 0)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("missing or invalid path/offset");
        return;
    }
    // M18 follow-up: LarisVMS.Web decides this (150 for a hover-scrub preview, 854 — ~480p on a
    // 16:9 source — for Pages/Snapshots' own cards), not the browser directly; the query param only
    // carries that decision across the proxy hop. Not part of the signed token (unlike path/offset)
    // since it's a quality knob, not something that needs tamper-protection — clamped rather than
    // trusted outright regardless. Folded into the cache filename below so a 150px and an 854px
    // request for the same offset never collide on the same cached file.
    var maxDimension = int.TryParse(ctx.Request.Query["maxDim"], out var md) ? Math.Clamp(md, 32, 1920) : LarisVMS.Media.ThumbnailCapture.DefaultMaxDimension;
    // ffmpeg -q:v, 2 (best) to 31 (worst). Same reasoning as maxDim above: LarisVMS.Web decides it,
    // this only carries the decision across the proxy hop, and it's clamped rather than trusted.
    var quality = int.TryParse(ctx.Request.Query["q"], out var qv) ? Math.Clamp(qv, 2, 31) : LarisVMS.Media.ThumbnailCapture.DefaultQuality;
    if (!MediaToken.TryValidateThumbnail(token, cameraId, path, offsetSeconds, currentKey, out var tokenError))
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

    string fullPath, mainDir, thumbsDir;
    try
    {
        fullPath = Path.GetFullPath(path);
        mainDir = Path.GetFullPath(Path.Combine(storageRoot, $"cam-{cameraId}", "main")) + Path.DirectorySeparatorChar;
        thumbsDir = Path.GetFullPath(Path.Combine(storageRoot, $"cam-{cameraId}", "thumbs"));
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!fullPath.StartsWith(mainDir, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var relativeToMain = Path.GetRelativePath(mainDir, fullPath);
    var thumbRelative = Path.ChangeExtension(relativeToMain, null) + $"_o{offsetSeconds:D2}_{maxDimension}q{quality}.jpg";
    var thumbPath = Path.Combine(thumbsDir, thumbRelative);

    if (File.Exists(thumbPath))
    {
        ctx.Response.ContentType = "image/jpeg";
        await ctx.Response.SendFileAsync(thumbPath, ctx.RequestAborted);
        return;
    }

    var bytes = await worker.CaptureThumbnailAsync(fullPath, offsetSeconds, ctx.RequestAborted, maxDimension, quality);
    if (bytes is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync("Could not extract a frame from this segment.");
        return;
    }

    // Atomic cache write shared with ThumbnailBackfillService — see SaveToCacheAsync's own doc
    // comment for why (a naive write straight to thumbPath let a concurrent reader see a
    // still-being-written, truncated file — confirmed live as the browser's broken-image icon
    // appearing right after "Loading…").
    await LarisVMS.Media.ThumbnailCapture.SaveToCacheAsync(thumbPath, bytes, CancellationToken.None);

    ctx.Response.ContentType = "image/jpeg";
    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
});

// Service restart — POSTed by LarisVMS.Web when an admin presses "Restart service" on the Nodes
// page. Same signed-token shape as every other Web->Node call, but scoped to the node itself rather
// than a camera (MediaToken.IssueForNodeControl binds the action name, not a cameraId).
//
// Responds *before* actually restarting: the restart stops this very process, so a response written
// afterward would never reach the caller and the Web tier would see a dropped connection instead of
// a result. TryRestartService only launches the detached helper and signals shutdown, so there is a
// real window to flush this response first.
app.MapPost("/restart", async (HttpContext ctx, NodeWorker worker, LarisVMS.Node.Update.UpdateService updateService) =>
{
    var token = ctx.Request.Query["token"].ToString();
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (!MediaToken.TryValidateNodeControl(token, "restart", currentKey, out var tokenError))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }

    if (!updateService.TryRestartService())
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("The updater binary this node needs to restart itself is missing. " +
            "Re-run install-node.ps1 on this machine.");
        return;
    }

    await ctx.Response.WriteAsync("Restarting.");
});

// Export trigger — POSTed by LarisVMS.Web's ExportJobDispatcher, never reached by a browser
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

// Export download side — serves exactly one finished export item's file to LarisVMS.Web's
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

// Exports page's trash button: lets the finished output be removed immediately instead of waiting
// on StorageManager's own 7-day export-retention sweep. Same path-prefix validation as the GET
// above, just with TryValidateExportDelete's own token family so a leaked download URL can't also
// delete the file it points to.
app.MapDelete("/export-file/{exportItemId:guid}", async (HttpContext ctx, Guid exportItemId, NodeWorker worker) =>
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
    if (!MediaToken.TryValidateExportDelete(token, exportItemId, path, currentKey, out var tokenError))
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

    if (!fullPath.StartsWith(exportsDir, StringComparison.OrdinalIgnoreCase))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    try { File.Delete(fullPath); }
    catch (IOException) { /* best-effort — StorageManager's own sweep is the backstop */ }

    ctx.Response.StatusCode = StatusCodes.Status204NoContent;
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

/// <summary>Copies exactly <paramref name="count"/> bytes from the current position of
/// <paramref name="source"/> to <paramref name="destination"/> — Stream has no built-in bounded
/// copy, only CopyToAsync's "copy everything left." Used to send just the init-segment prefix
/// (a source and a partial fetch share it, then diverge) without also serving whatever mdat bytes
/// happen to follow it.</summary>
static async Task CopyExactAsync(Stream source, Stream destination, long count, CancellationToken ct)
{
    var buffer = new byte[81920];
    var remaining = count;
    while (remaining > 0)
    {
        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = await source.ReadAsync(buffer.AsMemory(0, toRead), ct);
        if (read <= 0) break; // short read — nothing more to copy, caller's own CopyToAsync still runs after
        await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        remaining -= read;
    }
}
