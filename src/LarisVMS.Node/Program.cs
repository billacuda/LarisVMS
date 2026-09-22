using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
using LarisVMS.Relay;
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
    // Storage config is per-node with no global default — carry the storage path (and optional
    // archive path) install-node.ps1 was given into the register call so the new Node row has them
    // immediately. The full local fallback (ProgramData) is parsed below for the running process;
    // here we send only what was explicitly given, so an operator who omitted -StorageRoot lands in
    // the "no storage path" state on the server rather than silently registering ProgramData.
    var registerStorageRoot = GetArg(args, "--storage-root") ?? Environment.GetEnvironmentVariable("LARISVMS_STORAGE_ROOT");
    var registerArchiveRoot = GetArg(args, "--archive-root") ?? Environment.GetEnvironmentVariable("LARISVMS_ARCHIVE_ROOT");
    var response = await registerClient.RegisterAsync(
        new LarisVMS.Core.Dtos.NodeRegisterRequest(registrationKey, Environment.MachineName, NodeVersion.Current, "win-x64",
            registerStorageRoot, registerArchiveRoot),
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
    Console.Error.WriteLine($"ffmpeg could not be run at '{ffmpegPath}'. Install it with " +
        "'winget install ffmpeg --scope machine' (or any other means), then re-run — the node " +
        "auto-discovers a PATH or WinGet install. Override with --ffmpeg-path or LARISVMS_FFMPEG_PATH.");
    Environment.Exit(1);
    return;
}
Console.WriteLine($"Using ffmpeg: {ffmpegPath}");

var fallbackStorageRoot = GetArg(args, "--storage-root") ?? Environment.GetEnvironmentVariable("LARISVMS_STORAGE_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "recordings");

// M5 live view: this node's own Kestrel port for LarisVMS.Web to proxy live/playback through —
// plain HTTP, LAN-only. Originally the *only* media path (a browser never dialed a node directly).
// Since the failover plan's phase 1 a browser can also be pointed straight at the node over HTTPS on
// a second listener (see ClientEndpointConfig / CertHolder below), behind a toggle; this plain-HTTP
// port stays and is always bound. LastIpAddress (M4, captured server-side from this node's own
// outbound connections) plus this port is the address LarisVMS.Web dials.
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

// Thread-pool starvation mitigation for a measured, chronic fault: this process was observed
// freezing wholesale for 20s to nearly 3 minutes (2026-09-21: 25 stalls in 70 minutes, longest
// 176s), taking every live viewer's WebSocket send loop, every RecordingSession stdout drain and
// the report loops down together, while the ffmpeg children kept producing — so their output backed
// up and flooded on recovery, which downstream tore down and reconnected every live tile at once.
//
// NodeWorker.ProcessHealthLoopAsync measured what it was and was not: GC accounted for ~9ms of an
// 18.4s stall (heap small and flat, so not memory pressure), while ~57 work items sat queued behind
// only ~11 running threads. That is the signature of threads blocked rather than computing — and
// the reason those stalls run so long is the pool's own injection rate, which adds only ~1-2 threads
// per second once it is past its minimum. Raising the floor lets the pool create what it needs
// immediately instead of rationing threads for tens of seconds.
//
// This is a mitigation, not the cure: whatever is blocking those threads is still blocking them
// (ruled out so far — GC, StorageRetry's Thread.Sleep backoff, machine-wide stalls, and disk
// latency, the volume being a fully-expanded VHDX on NVMe). Naming the actual blocking call needs a
// stack dump captured during a stall. Costs nothing when unused: this sets the no-throttle ceiling,
// it does not preallocate threads.
ThreadPool.GetMinThreads(out var minWorkerThreads, out var minIoThreads);
ThreadPool.SetMinThreads(Math.Max(minWorkerThreads, 128), Math.Max(minIoThreads, 128));

var builder = WebApplication.CreateBuilder(args);
// M11: a Windows Service has no console anyone will ever see — file capture is the only way to
// diagnose a node after the fact. Sibling of node.config's own %ProgramData%\LarisVMS\ (NodeConfigStore),
// already proven writable by this same service account. StorageManager.SweepLogsDirectory (fixed
// 14-day window) is this tier's retention sweep, run alongside its other periodic disk cleanup.
var nodeFileLogger = new FileLoggerProvider(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "logs"),
    "node", LogLevel.Information);
builder.Logging.AddProvider(nodeFileLogger);
// Kestrel/routing/result request-pipeline noise — the node's own LAN-facing media routes and its
// localhost control channel both log "Request starting / Executing endpoint / ... / Request
// finished" per hit, and Hosting.Lifetime prints a three-line startup banner. Hidden while the
// deployment-wide log level is Information or higher; drop it to Debug/Trace to get them back.
var frameworkLogFilter = FrameworkLogFilter.HiddenUnlessDebug(nodeFileLogger);
builder.Logging.AddFilter("Microsoft.AspNetCore", frameworkLogFilter);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", frameworkLogFilter);

// Failover plan phase 1: the optional second, client-facing Kestrel listener — a browser connecting
// straight to this node for live/playback over HTTPS. Config is the local client-endpoint.json
// merged over the last server poll (local wins); resolving the *listener* is a restart-time decision
// (the cert itself hot-reloads — see CertWatcherService). The plain-HTTP LAN port (livePort) is
// always bound; the HTTPS port only when a certificate actually resolves.
var clientEndpoint = ClientEndpointConfig.Resolve(config.CachedConfig);
CertHolder? certHolder = null;
if (clientEndpoint.Enabled)
{
    var selfSignedPfx = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "client-endpoint.selfsigned.pfx");
    certHolder = new CertHolder(
        new CertHolderOptions(clientEndpoint.PfxPath, clientEndpoint.PfxPassword, clientEndpoint.AllowInsecure,
            clientEndpoint.Host, selfSignedPfx),
        nodeFileLogger.CreateLogger("ClientEndpoint"));
    if (certHolder.Load())
        Console.WriteLine($"Client HTTPS endpoint: binding port {clientEndpoint.Port} " +
            $"({(certHolder.IsSelfSigned ? "self-signed" : "supplied certificate")}).");
    else
        Console.Error.WriteLine($"Client HTTPS endpoint enabled but not starting: {certHolder.LastError}");
}

builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(livePort);
    if (certHolder is { Current: not null })
        o.ListenAnyIP(clientEndpoint.Port, lo => lo.UseHttps(h =>
            h.ServerCertificateSelector = (_, _) => certHolder.Current));
});
builder.Services.AddWindowsService(o => o.ServiceName = "LarisVMS Node");
builder.Services.AddSingleton(apiClient);
// Failover plan phase 5c: rejects a second use of a one-shot Web→Node control token (v2 tokens only;
// v1 and the multi-use live-view token pass a null jti and are unaffected).
builder.Services.AddSingleton<SeenTokenCache>();
if (certHolder is not null)
{
    builder.Services.AddSingleton(certHolder);
    if (certHolder.Current is not null)
        builder.Services.AddSingleton<IHostedService>(sp => new CertWatcherService(
            certHolder, sp.GetRequiredService<ILoggerFactory>().CreateLogger<CertWatcherService>()));
}
builder.Services.AddSingleton(sp => new UpdateService(
    config, insecureTls, sp.GetRequiredService<ILoggerFactory>().CreateLogger<UpdateService>(),
    sp.GetRequiredService<IHostApplicationLifetime>()));
// Failover plan phase 3: recording-failover quorum — this node probes the /health of any node it is
// the backup for and reports the verdict each heartbeat.
builder.Services.AddSingleton<PartnerHealthTracker>();
builder.Services.AddSingleton<IHostedService>(sp => new PartnerProbeService(
    sp.GetRequiredService<PartnerHealthTracker>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<PartnerProbeService>()));
builder.Services.AddSingleton(sp => new NodeWorker(
    apiClient, ffmpegPath, fallbackStorageRoot, livePort, config, sp.GetRequiredService<ILoggerFactory>(), onvifEventsClient,
    sp.GetRequiredService<UpdateService>(), nodeFileLogger, clientEndpoint, certHolder,
    sp.GetRequiredService<PartnerHealthTracker>()));
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

// Failover plan phase 3: unauthenticated liveness probe on the plain-HTTP LAN port. A quorum voter
// (the partner node, central, an assigned proxy) hits this to decide "is the recorder service
// actually running" — a real service check, deliberately not a ping: a lingering socket or a load
// balancer answering for a dead process must read as down, so this returns a parseable body the
// caller validates, not just a 200. No auth: it exposes only version + recording count + uptime, all
// low-sensitivity, and a voter can't present a node bearer secret. Mirrors the proxy's own /health.
var nodeProcessStartUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
app.MapGet("/health", (NodeWorker worker) => Results.Json(new NodeHealthDto(
    NodeVersion.Current, worker.ActiveRecordingCount,
    (long)(DateTime.UtcNow - nodeProcessStartUtc).TotalSeconds)));

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
    var token = ExtractToken(ctx);
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
    var servingSub = session is not null;
    session ??= worker.TryGetSession(cameraId);
    if (session is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("Camera is not currently recording on this node.");
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();

    // Failover plan phase 1: log every viewer that attaches to a camera's live stream and how long it
    // stayed. A browser connecting directly (direct mode) sends an Origin header on the WS handshake;
    // LarisVMS.Web's own proxy ClientWebSocket does not — so this distinguishes a real client from a
    // relayed one without a second signal.
    var origin = ctx.Request.Headers.Origin.ToString();
    var via = string.IsNullOrEmpty(origin) ? "proxy" : $"direct from {origin}";
    var roleLabel = servingSub ? "Sub" : "Main";
    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    var cameraName = worker.CameraDisplayName(cameraId);
    var startedAt = DateTime.UtcNow;
    liveLogger.LogInformation("Live stream started: {CameraName} ({Role}) -> client {ClientIp} ({Via}).",
        cameraName, roleLabel, clientIp, via);
    try
    {
        await LiveViewerHandler.RunAsync(socket, session, liveLogger, ctx.RequestAborted);
    }
    finally
    {
        liveLogger.LogInformation("Live stream ended: {CameraName} ({Role}) -> client {ClientIp} ({Via}) after {Seconds:F0}s.",
            cameraName, roleLabel, clientIp, via, (DateTime.UtcNow - startedAt).TotalSeconds);
    }
});

// Object detection plan decision 6: live-view box overlay — a separate WS from the video stream
// above, reusing the exact same MediaToken this camera's own /live token already grants (viewing a
// camera's boxes is the same authorization boundary as viewing its video, not a separate one).
app.Map("/live/{cameraId:guid}/detections", async (HttpContext ctx, Guid cameraId, NodeWorker worker) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var token = ExtractToken(ctx);
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

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await DetectionOverlayHandler.RunAsync(socket, cameraId, worker.VisionHttpClient, liveLogger, ctx.RequestAborted);
});

// Object detection plan pass 3c-1: live per-zone motion score wash on the Zones editor — reuses the
// same /live token family as the two routes above (Node has no separate notion of "can edit zones,"
// that's the Web tier's own Cameras.Edit gate one layer out; this token only proves "some authorized
// viewer of this camera's media requested this").
app.Map("/live/{cameraId:guid}/motion-zones", async (HttpContext ctx, Guid cameraId, NodeWorker worker) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var token = ExtractToken(ctx);
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

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await MotionZoneOverlayHandler.RunAsync(socket, cameraId, worker, liveLogger, ctx.RequestAborted);
});

// M7 playback: serves exactly one segment file's raw bytes. Normally reached via LarisVMS.Web's
// proxy ("IIS proxies every byte", same as /live); since the failover plan's phase 1, when direct
// streaming is on, Web instead 302-redirects the browser straight here and the request arrives
// cross-origin — hence the CORS headers below (a simple GET, no preflight; X-Fragment-Start-Seconds
// must be exposed so playback-player.js can still read it). The token binds cameraId + this exact
// path, so path can't be tampered with independently of the signature; the directory-prefix check
// below is a second, independent line of defense.
app.MapGet("/playback-segment/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, NodeWorker worker, SeenTokenCache seenTokens) =>
{
    // These routes carry no cookies and authorize on the signed ?token= alone, so a reflected (or
    // wildcard) allow-origin is safe — it only lets the already-authorized browser read the response.
    var origin = ctx.Request.Headers.Origin.ToString();
    ctx.Response.Headers.AccessControlAllowOrigin = string.IsNullOrEmpty(origin) ? "*" : origin;
    ctx.Response.Headers["Access-Control-Expose-Headers"] = "X-Fragment-Start-Seconds";
    if (!string.IsNullOrEmpty(origin)) ctx.Response.Headers["Vary"] = "Origin";

    var token = ExtractToken(ctx);
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
    if (!MediaToken.TryValidateSegment(token, cameraId, path, currentKey, out var tokenError, out var tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }
    // Failover plan phase 5c: one segment fetch per token. Web mints a fresh token per request, so a
    // repeat here is a replay. v1 tokens (older Web) carry no jti and skip this.
    if (!seenTokens.TryConsume(tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("token already used");
        return;
    }

    // Failover plan phase 1: a browser hitting this directly (direct mode) arrives cross-origin with
    // an Origin header — LarisVMS.Web's proxy does not. Log the direct fetches at Information so an
    // operator can see the node is serving playback straight to clients; proxied fetches stay at
    // Debug (there's already an audit trail for those on the Web tier).
    if (!string.IsNullOrEmpty(origin))
        playbackLogger.LogInformation("Direct playback: {CameraName} -> client {ClientIp} (origin {Origin}), segment {File}.",
            worker.CameraDisplayName(cameraId), ctx.Connection.RemoteIpAddress?.ToString() ?? "?", origin, Path.GetFileName(path));
    else
        playbackLogger.LogDebug("Proxied playback: {CameraName}, segment {File}.", worker.CameraDisplayName(cameraId), Path.GetFileName(path));

    var storageRoot = worker.StorageRoot;
    if (storageRoot is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }

    string fullPath;
    try
    {
        fullPath = Path.GetFullPath(path);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Path must sit under this camera's cam-{id}/main on the primary OR the archive root — an
    // archived segment's row carries the archive path but is served the same way.
    if (!MediaPathResolver.IsAllowed(fullPath, cameraId, storageRoot, worker.ArchiveRoot) || !File.Exists(fullPath))
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
app.MapGet("/playback-thumbnail/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, NodeWorker worker, SeenTokenCache seenTokens, ILogger<Program> logger) =>
{
    var token = ExtractToken(ctx);
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
    if (!MediaToken.TryValidateThumbnail(token, cameraId, path, offsetSeconds, currentKey, out var tokenError, out var tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }
    // Failover plan phase 5c — one frame per token. Web mints one per request (see ProxyThumbnailAsync).
    if (!seenTokens.TryConsume(tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("token already used");
        return;
    }

    var storageRoot = worker.StorageRoot;
    if (storageRoot is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }

    string fullPath;
    try
    {
        fullPath = Path.GetFullPath(path);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Diagnostic-only logging (no behavior change): a card whose Playback link plays fine but whose
    // thumbnail 404s/502s has no other way to tell which of these three causes fired, since none of
    // them previously logged anything at all. The path can be under this node's primary OR archive
    // root — the cache dirs are derived from whichever matched.
    if (!MediaPathResolver.TryResolve(fullPath, cameraId, storageRoot, worker.ArchiveRoot, out var mainDir, out var thumbsDir, out _))
    {
        logger.LogWarning(
            "Playback-thumbnail 404: requested path {FullPath} is not under this node's primary or archive main directory for cam-{CameraId} (StorageRoot={StorageRoot}, ArchiveRoot={ArchiveRoot}) — likely a Segment.FilePath recorded under a since-changed storage root.",
            fullPath, cameraId, storageRoot, worker.ArchiveRoot);
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!File.Exists(fullPath))
    {
        logger.LogWarning("Playback-thumbnail 404: segment file {FullPath} does not exist on disk despite a Segment row pointing at it.", fullPath);
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var relativeToMain = Path.GetRelativePath(mainDir, fullPath);
    // Cache-file stem (no extension). The trailing "q{quality}" is still a bucket key even for a
    // WebP file — it distinguishes the 150/q8 hover preview from the 1280/q4 "exact" card — the
    // WebP encode itself uses its own fixed quality (ThumbnailCapture.DefaultWebpQuality).
    var thumbStem = Path.Combine(thumbsDir,
        Path.ChangeExtension(relativeToMain, null) + $"_o{offsetSeconds:D2}_{maxDimension}q{quality}");

    var cached = CachedImageFormat.FindExisting(thumbStem);
    if (cached is not null)
    {
        ctx.Response.ContentType = CachedImageFormat.ContentType(cached);
        await ctx.Response.SendFileAsync(cached, ctx.RequestAborted);
        return;
    }

    var webp = worker.WebpSupported;
    var bytes = await worker.CaptureThumbnailAsync(fullPath, offsetSeconds, ctx.RequestAborted, maxDimension, quality, webp);
    if (bytes is null)
    {
        logger.LogWarning("Playback-thumbnail 502: ffmpeg produced no frame for {FullPath} at offset {OffsetSeconds}s (timeout, corrupt segment, or offset beyond content).", fullPath, offsetSeconds);
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync("Could not extract a frame from this segment.");
        return;
    }

    var thumbPath = thumbStem + CachedImageFormat.Extension(webp);
    // Atomic cache write shared with ThumbnailBackfillService — see SaveToCacheAsync's own doc
    // comment for why (a naive write straight to thumbPath let a concurrent reader see a
    // still-being-written, truncated file — confirmed live as the browser's broken-image icon
    // appearing right after "Loading…").
    await LarisVMS.Media.ThumbnailCapture.SaveToCacheAsync(thumbPath, bytes, CancellationToken.None);

    ctx.Response.ContentType = CachedImageFormat.ContentType(thumbPath);
    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
});

// Object detection plan decision 10: same proxy-target shape as /playback-thumbnail above (signed
// token binds cameraId+path+offsetSeconds; directory-prefix check is the second, independent line of
// defense) but crops to a detection box and caches under cam-{id}/snapshots/ instead of thumbs/,
// keyed by the owning MotionSpan's own id rather than a bucketed offset — a snapshot has exactly one
// owning span, so this is simpler and collision-free without needing an offset-bucketing scheme.
// Reuses MediaToken's existing thumbnail token pair rather than minting a new one: the
// security-sensitive fields (which file, which offset) are identical in shape and meaning to the
// hover-thumbnail token's own boundary. The crop box/frame dimensions ride as unsigned query params,
// same "a quality knob, not something that needs tamper-protection" reasoning maxDim/q already use
// above — a tampered box only changes what crop of an already-authorized frame comes back, never
// which file or offset is read.
app.MapGet("/snapshot-image/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, NodeWorker worker, SeenTokenCache seenTokens, ILogger<Program> logger) =>
{
    var token = ExtractToken(ctx);
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
    if (!MediaToken.TryValidateThumbnail(token, cameraId, path, offsetSeconds, currentKey, out var tokenError, out var tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }
    // Failover plan phase 5c — one crop per token (Web mints one per request).
    if (!seenTokens.TryConsume(tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("token already used");
        return;
    }

    if (!long.TryParse(ctx.Request.Query["spanId"], out var spanId)
        || !double.TryParse(ctx.Request.Query["x"], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxX)
        || !double.TryParse(ctx.Request.Query["y"], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxY)
        || !double.TryParse(ctx.Request.Query["w"], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxW)
        || !double.TryParse(ctx.Request.Query["h"], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxH)
        || !int.TryParse(ctx.Request.Query["frameW"], out var frameW) || frameW <= 0
        || !int.TryParse(ctx.Request.Query["frameH"], out var frameH) || frameH <= 0)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("missing or invalid spanId/box/frame dimensions");
        return;
    }

    // Optional, unsigned — same "changes only which frame within an already-authorized ~1s window"
    // reasoning the box params use. offsetMs gives the crop seek sub-second precision the
    // whole-second token value can't; bestFrameTicks lets us serve a pre-cropped eager snapshot
    // (written by the high-res re-detection path) instead of re-cropping the recorded segment.
    // Both absent = an older Web tier: fall back to offsetSeconds and skip the eager-crop lookup.
    var offsetMs = long.TryParse(ctx.Request.Query["offsetMs"], out var parsedMs) && parsedMs >= 0
        ? parsedMs : offsetSeconds * 1000L;
    long.TryParse(ctx.Request.Query["bestFrameTicks"], out var bestFrameTicks);

    var storageRoot = worker.StorageRoot;
    if (storageRoot is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }

    string fullPath;
    try
    {
        fullPath = Path.GetFullPath(path);
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Diagnostic-only logging (no behavior change) — see /playback-thumbnail's own identical comment.
    // Primary or archive root; cache dir derived from whichever matched.
    if (!MediaPathResolver.TryResolve(fullPath, cameraId, storageRoot, worker.ArchiveRoot, out var mainDir, out _, out var snapshotsDir))
    {
        logger.LogWarning(
            "Snapshot-image 404 for span {SpanId}: requested path {FullPath} is not under this node's primary or archive main directory for cam-{CameraId} (StorageRoot={StorageRoot}, ArchiveRoot={ArchiveRoot}) — likely a Segment.FilePath recorded under a since-changed storage root.",
            spanId, fullPath, cameraId, storageRoot, worker.ArchiveRoot);
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (!File.Exists(fullPath))
    {
        logger.LogWarning("Snapshot-image 404 for span {SpanId}: segment file {FullPath} does not exist on disk despite a Segment row pointing at it.", spanId, fullPath);
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var relativeToMain = Path.GetRelativePath(mainDir, fullPath);
    var snapshotStem = Path.Combine(snapshotsDir, Path.ChangeExtension(relativeToMain, null) + $"_span{spanId}");

    var cachedSnapshot = CachedImageFormat.FindExisting(snapshotStem);
    if (cachedSnapshot is not null)
    {
        ctx.Response.ContentType = CachedImageFormat.ContentType(cachedSnapshot);
        await ctx.Response.SendFileAsync(cachedSnapshot, ctx.RequestAborted);
        return;
    }

    // Eager crop cropped from the exact frame the detection ran on (the high-res Main-stream frame,
    // or — pass G — this camera's own Sub-stream detection frame), so it lines up with the object
    // far better than a whole-second seek into the recorded segment can. Copied (not moved) into the
    // canonical span-keyed snapshot cache on first view so retention + the orphaned-snapshot sweep
    // govern that copy like every other snapshot; the staging file is left in place so a second span
    // sharing the same instant (a person and a dog in one frame) can promote it too, and
    // StorageManager.SelectExpiredStagedCrops ages the stagers out after a few days.
    if (bestFrameTicks > 0)
    {
        var stagedPath = CachedImageFormat.FindExisting(Path.Combine(snapshotsDir, "hires", bestFrameTicks.ToString()));
        if (stagedPath is not null)
        {
            try
            {
                var staged = await File.ReadAllBytesAsync(stagedPath, ctx.RequestAborted);
                if (staged.Length > 0)
                {
                    var promoted = snapshotStem + Path.GetExtension(stagedPath);
                    await LarisVMS.Media.ThumbnailCapture.SaveToCacheAsync(promoted, staged, CancellationToken.None);
                    ctx.Response.ContentType = CachedImageFormat.ContentType(promoted);
                    await ctx.Response.Body.WriteAsync(staged, ctx.RequestAborted);
                    return;
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Snapshot-image: eager crop {StagedPath} for span {SpanId} could not be read — falling back to a segment crop.", stagedPath, spanId);
            }
        }
    }

    var webp = worker.WebpSupported;
    var bytes = await worker.CaptureSnapshotImageAsync(fullPath, offsetMs / 1000.0, boxX, boxY, boxW, boxH, frameW, frameH, ctx.RequestAborted, webp);
    if (bytes is null)
    {
        logger.LogWarning(
            "Snapshot-image 502 for span {SpanId}: ffmpeg produced no cropped frame for {FullPath} at offset {OffsetMs}ms, box ({BoxX},{BoxY},{BoxW},{BoxH}) against frame {FrameW}x{FrameH} (timeout, corrupt segment, offset beyond content, or a crop rectangle that fell outside the frame).",
            spanId, fullPath, offsetMs, boxX, boxY, boxW, boxH, frameW, frameH);
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync("Could not extract a cropped frame from this segment.");
        return;
    }

    // Same atomic cache write as /playback-thumbnail above.
    var snapshotPath = snapshotStem + CachedImageFormat.Extension(webp);
    await LarisVMS.Media.ThumbnailCapture.SaveToCacheAsync(snapshotPath, bytes, CancellationToken.None);

    ctx.Response.ContentType = CachedImageFormat.ContentType(snapshotPath);
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
app.MapPost("/restart", async (HttpContext ctx, NodeWorker worker, SeenTokenCache seenTokens, LarisVMS.Node.Update.UpdateService updateService) =>
{
    var token = ExtractToken(ctx);
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (!MediaToken.TryValidateNodeControl(token, "restart", currentKey, out var tokenError, out var tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }
    // Failover plan phase 5c — a restart command can't be replayed within its 30s window.
    if (!seenTokens.TryConsume(tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("token already used");
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

// Wakes NodeWorker's reconcile loop immediately instead of waiting up to 30s for its own timer — the
// hot-swap logic (ReconcileVision's signature comparison -> CameraPipelineManager.StartOrReplaceAsync)
// is unchanged, this only shortens how soon the loop gets around to re-running it. Same signed-token/
// anti-replay shape as /restart, but far simpler: no process restart, no updater dependency, so a
// briefly-unreachable node just means the 30s poll remains the correctness backstop.
app.MapPost("/reconcile-now", async (HttpContext ctx, NodeWorker worker, SeenTokenCache seenTokens) =>
{
    var token = ExtractToken(ctx);
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (!MediaToken.TryValidateNodeControl(token, "reconcile-now", currentKey, out var tokenError, out var tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }
    if (!seenTokens.TryConsume(tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("token already used");
        return;
    }

    worker.RequestImmediateReconcile();
    await ctx.Response.WriteAsync("Reconciling.");
});

// Proxies Vision Service's own GET /models — the data source for Admin's Detection.LocalModelName
// dropdown (Admin/Nodes.cshtml probes this directly; Admin/Settings/Detection.cshtml.cs fans it out
// across every online node). Same signed-token/anti-replay shape as /restart, but read-only and far
// simpler — no process restart, no updater dependency, and a Vision-Service-unreachable node just
// means detection is off/not yet installed, not a real error worth a 5xx.
app.MapGet("/vision/models", async (HttpContext ctx, NodeWorker worker, SeenTokenCache seenTokens) =>
{
    var token = ExtractToken(ctx);
    var currentKey = worker.MediaSigningKey;
    if (currentKey is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("Node hasn't completed its first reconcile cycle yet — try again shortly.");
        return;
    }
    if (!MediaToken.TryValidateNodeControl(token, "vision-models", currentKey, out var tokenError, out var tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync(tokenError);
        return;
    }
    if (!seenTokens.TryConsume(tokenJti))
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsync("token already used");
        return;
    }

    try
    {
        var models = await worker.VisionHttpClient.GetFromJsonAsync<List<DiscoveredModelDto>>("/models");
        await ctx.Response.WriteAsJsonAsync(models ?? []);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        // Vision Service isn't running (no AI detection installed/enabled on this node) — an empty
        // list reads the same as "no models found", not an error, to the Admin picker.
        await ctx.Response.WriteAsJsonAsync(Array.Empty<DiscoveredModelDto>());
    }
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
    var token = ExtractToken(ctx);
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

    var validatedPaths = new List<string>();
    try
    {
        foreach (var p in request.SegmentFilePaths)
        {
            var fullPath = Path.GetFullPath(p);
            // A range that spans an archived segment carries the archive path — allowed the same way.
            if (!MediaPathResolver.IsAllowed(fullPath, cameraId, storageRoot, worker.ArchiveRoot))
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
    var token = ExtractToken(ctx);
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
    var token = ExtractToken(ctx);
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
    var token = ExtractToken(ctx);
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

// Object detection plan decision 3: LarisVMS.Vision.Service's own report of one closed/checkpointed
// AI-detection span, POSTed back here rather than through the normal Web-facing MediaToken auth
// every other route on this port uses — Vision Service is a sibling process on the same machine,
// not something LarisVMS.Web ever calls. The trust boundary is the OS's own loopback isolation
// instead: the caller must be reaching this from 127.0.0.1/::1, checked explicitly since this
// Kestrel host also listens on every interface (ListenAnyIP) for the LAN-facing routes above it.
app.MapPost("/detections", async (HttpContext ctx, VisionDetectionReportItem item, NodeWorker worker) =>
{
    if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.None))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("This endpoint only accepts connections from the local machine.");
        return;
    }

    worker.ReportVisionDetection(item);
});

// A1 of the snapshot-alignment work: LarisVMS.Vision.Service ships a snapshot cropped from the
// exact Main-stream frame a high-res re-detection ran against. Same loopback-only trust boundary as
// /detections above. Staged by best-frame ticks under cam-{id}/snapshots/hires/ — the
// /snapshot-image route promotes it into the span-keyed cache (retention-governed) on first view.
app.MapPost("/detections/crop", async (HttpContext ctx, VisionDetectionCropItem item, NodeWorker worker, ILogger<Program> logger) =>
{
    if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.None))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("This endpoint only accepts connections from the local machine.");
        return;
    }

    var storageRoot = worker.StorageRoot;
    if (storageRoot is null) { ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
    if (item.Image is not { Length: > 0 }) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }

    try
    {
        var dir = Path.Combine(storageRoot, $"cam-{item.CameraId}", "snapshots", "hires");
        Directory.CreateDirectory(dir);
        // The eager crop is always WebP — SkiaSharp encodes it directly in the Vision Service, no
        // ffmpeg/libwebp probe involved (see CameraDetectionPipeline.EagerCropWebpQuality).
        var tmp = Path.Combine(dir, $"{item.AtUtc.Ticks}{CachedImageFormat.WebpExtension}.tmp");
        var final = Path.Combine(dir, $"{item.AtUtc.Ticks}{CachedImageFormat.WebpExtension}");
        await File.WriteAllBytesAsync(tmp, item.Image, ctx.RequestAborted);
        File.Move(tmp, final, overwrite: true);
        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }
    catch (IOException ex)
    {
        logger.LogWarning(ex, "Could not stage an eager snapshot crop for camera {CameraId} — that span will fall back to a segment-seek crop.", item.CameraId);
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    }
});

// Loopback-only proxy so the sibling Vision Service can fetch a detection model it doesn't have
// cached without needing the server URL or node credentials of its own — the node passes the
// request through on its authenticated channel. Only YOLOX models go this way (D-FINE is bundled).
app.MapGet("/internal/detection-model/{family}/{variant}", async (HttpContext ctx, string family, string variant, NodeApiClient api) =>
{
    if (!IPAddress.IsLoopback(ctx.Connection.RemoteIpAddress ?? IPAddress.None))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("This endpoint only accepts connections from the local machine.");
        return;
    }

    try
    {
        await using var upstream = await api.OpenDetectionModelStreamAsync(family, variant, ctx.RequestAborted);
        ctx.Response.ContentType = "application/octet-stream";
        await upstream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync($"Could not obtain the {family}/{variant} model from the server: {ex.Message}");
    }
});

// Failover plan phase 1: a friendly landing page for the "open https://{node}:{port}/ and trust the
// certificate" step the client shows when direct streaming is running on a self-signed cert.
app.MapGet("/", () => Results.Content(
    "LarisVMS recorder node. If you opened this page to trust its certificate, that's done — you can close this tab.",
    "text/plain"));

await app.RunAsync();

static string? GetArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

/// <summary>Media token, from an `Authorization: Bearer {token}` header if the caller sent one, else
/// the legacy `?token=` query param — Web now sends both (see Web's own token-issuing call sites)
/// specifically so this node and a not-yet-updated one can both be running during a rollout without
/// either rejecting the other's requests. Header preferred once present: it's the one that actually
/// stops the token from landing in access logs and proxy logs the way a query string does, which is
/// the whole reason for adding it. Once every node in the fleet has updated past this build, Web can
/// stop sending the query param and this fallback can be deleted.</summary>
static string? ExtractToken(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Bearer ", StringComparison.Ordinal)) return header["Bearer ".Length..];
    var queryToken = ctx.Request.Query["token"].ToString();
    return string.IsNullOrEmpty(queryToken) ? null : queryToken;
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
