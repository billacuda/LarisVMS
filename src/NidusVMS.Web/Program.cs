using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NidusVMS.Core;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Enums;
using NidusVMS.Core.Interfaces;
using NidusVMS.Core.Security;
using NidusVMS.Infrastructure.Auth;
using NidusVMS.Infrastructure.Data;
using NidusVMS.Infrastructure.Repositories;
using NidusVMS.Infrastructure.Security;
using NidusVMS.Infrastructure.Services;
using NidusVMS.Web.Health;
using NidusVMS.Web.Middleware;
using NidusVMS.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
// Holds only what the app needs before the database can be read: the connection string and
// branding. Everything else lives in the Settings table via ISettingsResolver. Written by the
// setup wizard (SetupService.MergeSetupJson), never committed to source control.
builder.Configuration.AddJsonFile("setup-generated.json", optional: true, reloadOnChange: true);

// ── Database ──────────────────────────────────────────────────────────────────
// Conditional registration: pre-setup there is no connection string yet, and the app must still
// boot so the wizard can run.
builder.Services.AddDbContext<ApplicationDbContext>((provider, options) =>
{
    var cs = provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
    if (!string.IsNullOrWhiteSpace(cs))
        options.UseSqlServer(cs);
});

// ── Data Protection ───────────────────────────────────────────────────────────
// Protects auth cookies and, via SecretProtection, camera/SMB credentials and node media signing
// keys at rest. SetApplicationName is explicit rather than derived from the content root path, so
// moving/renaming the IIS site doesn't silently change the key isolation scope. deploy.ps1 excludes
// this folder from its /MIR mirror — see deploy.ps1 and the README's "Data at rest" section.
//
// Deliberately still "Rcordr", not "NidusVMS": this string is the key-ring isolation discriminator,
// not a display name — it's baked into every existing encrypted value (camera/SMB credentials, node
// media signing keys), and changing it makes all of that permanently undecryptable (confirmed live:
// CryptographicException "The payload was invalid" the moment this got swept by the project's
// Rcordr->NidusVMS rename along with everything else). No user ever sees this string. If it's ever
// changed, every already-encrypted value needs re-encrypting first — not a find-and-replace.
var dataProtectionPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NidusVMS", "keys");
EnsureWritableDirectory(dataProtectionPath);

var dpBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("Rcordr");

if (OperatingSystem.IsWindows())
{
#pragma warning disable CA1416
    dpBuilder.ProtectKeysWithDpapi(protectToLocalMachine: true);
#pragma warning restore CA1416
}

// ── Identity ──────────────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequireDigit = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequiredLength = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders()
    .AddDefaultUI();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.AccessDeniedPath = "/Identity/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromMinutes(60);
    options.SlidingExpiration = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

    options.Events.OnSignedIn = async ctx =>
    {
        var audit = ctx.HttpContext.RequestServices.GetService<IAuditService>();
        if (audit is null) return;
        var userId = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userName = ctx.Principal?.Identity?.Name;
        var ip = ctx.HttpContext.Connection.RemoteIpAddress?.ToString();
        await audit.LogAsync("Login.Success", userId, userName, ip);
    };

    options.Events.OnSigningOut = async ctx =>
    {
        var audit = ctx.HttpContext.RequestServices.GetService<IAuditService>();
        if (audit is null) return;
        var userId = ctx.HttpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var userName = ctx.HttpContext.User.Identity?.Name;
        var ip = ctx.HttpContext.Connection.RemoteIpAddress?.ToString();
        await audit.LogAsync("Logout", userId, userName, ip);
    };
});

// ── Authorization / RBAC ──────────────────────────────────────────────────────
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdministratorOnly", policy => policy.RequireRole("Administrator"));
});
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
// Resolves any "{Resource}.{Action}" policy name (e.g. "Cameras.Edit") on the fly, instead of
// requiring every resource × action combination in the RBAC matrix to be individually registered
// with AddPolicy above.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
builder.Services.AddScoped<ISetupService, SetupService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ISettingsResolver, SettingsResolver>();
builder.Services.AddScoped<ICameraDiscoveryService, CameraDiscoveryService>();
builder.Services.AddScoped<ICameraService, CameraService>();
builder.Services.AddScoped<ICameraGroupService, CameraGroupService>();
builder.Services.AddScoped<INodeService, NodeService>();
builder.Services.AddScoped<INodeBuildService, NodeBuildService>();
builder.Services.AddScoped<IViewService, ViewService>();
builder.Services.AddScoped<ITimelineService, TimelineService>();
builder.Services.AddScoped<IZoneService, ZoneService>();
builder.Services.AddScoped<IEventTagRuleService, EventTagRuleService>();
builder.Services.AddScoped<IExportService, ExportService>();

// First Web-tier BackgroundService — see ExportJobDispatcher's own doc comment for why exports
// needed one instead of a synchronous per-camera download.
builder.Services.AddHostedService<ExportJobDispatcher>();

// ── ONVIF HTTP client ────────────────────────────────────────────────────────
// CameraService takes a Func<HttpClient> rather than IHttpClientFactory directly so
// NidusVMS.Infrastructure doesn't need a package reference just for the factory interface — Web
// already has it via the ASP.NET Core shared framework and resolves it here.
//
// Certificate validation is disabled for this client only. LAN ONVIF cameras that support HTTPS
// almost universally use a self-signed certificate generated by the device itself — there is no CA
// infrastructure for them to use, so standard chain validation rejects every one of them
// unconditionally, not just misconfigured ones. This client is never used for anything but talking
// to cameras the user has explicitly added by IP/hostname on their own network, so the usual
// MITM concern a browser's cert warning protects against doesn't apply here the same way. Scoped
// narrowly to this named client so it can't accidentally weaken TLS for any other outbound call.
builder.Services.AddHttpClient("onvif", client => client.Timeout = TimeSpan.FromSeconds(8))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    });
builder.Services.AddScoped<Func<HttpClient>>(sp =>
    () => sp.GetRequiredService<IHttpClientFactory>().CreateClient("onvif"));

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<DbHealthCheck>("database", failureStatus: HealthStatus.Unhealthy);

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToFolder("/Setup");
});

var app = builder.Build();

// Wire the Data Protection provider into the DB-column encryption helper before any DB access.
SecretProtection.Configure(app.Services.GetRequiredService<IDataProtectionProvider>());

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSecurityHeaders();
app.UseSetupRedirect();

app.UseRouting();
// Needed before UseAuthorization so the /live WS upgrade request survives the pipeline as a
// WebSocket rather than being treated as a normal HTTP request that happens to ask for an upgrade.
app.UseWebSockets();
app.UseAuthentication();
// Runs after cookie auth but before authorization, mirroring dploid's AgentAuthMiddleware
// placement — it establishes the node principal (via HttpContext.Items, not a ClaimsPrincipal,
// since these endpoints don't carry [Authorize] policies) independently of the cookie scheme.
app.UseMiddleware<NodeAuthMiddleware>();
app.UseAuthorization();

app.MapRazorPages();
app.MapHealthChecks("/health").AllowAnonymous();

// ── Node control plane ──────────────────────────────────────────────────────
// /register is anonymous (authenticates with the one-time registration key in the body instead);
// every other route is authenticated by NodeAuthMiddleware above and reads the Node it attached to
// HttpContext.Items rather than from a ClaimsPrincipal.
var nodesApi = app.MapGroup("/api/nodes");

nodesApi.MapPost("/register", async (NodeRegisterRequest request, INodeService nodeService, CancellationToken ct) =>
{
    try
    {
        return Results.Json(await nodeService.RegisterAsync(request, ct));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Unauthorized();
    }
});

nodesApi.MapPost("/heartbeat", async (HttpContext ctx, NodeHeartbeatRequest request, INodeService nodeService,
    INodeBuildService nodeBuildService, ISettingsResolver settings, CancellationToken ct) =>
{
    // LastSeenAt/Status/LastIpAddress are already updated by NodeAuthMiddleware's AuthenticateAsync
    // call for every authenticated request. Version/LivePort can only be updated here, not in the
    // middleware — middleware runs before this handler's request body is bound, so it has nothing
    // reported to stamp; this is the one place NodeHeartbeatRequest's fields are actually read.
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await nodeService.RecordHeartbeatAsync(node.Id, request.FreeBytes, request.TotalBytes, request.Version, request.LivePort, ct);

    // ── Auto-update check ────────────────────────────────────────────────
    // Global-only gate (Admin/Settings' NodeAutoUpdate.Enabled) — no per-node override for this
    // first pass, unlike Retention/Recording.Mode which do walk Camera -> Node -> Global.
    NodeUpdateInfoDto? updateAvailable = null;
    var autoUpdateEnabled = await settings.GetAsync("NodeAutoUpdate.Enabled", true, ct: ct);
    if (autoUpdateEnabled)
    {
        // node.Platform is set once at registration (NodeRegisterRequest.Platform) and never
        // updated by a heartbeat — same as dploid's agent.OSPlatform. Falls back to "win-x64" for
        // an already-registered node whose Platform is still unset/null (pre-dates this feature).
        var platform = node.Platform ?? "win-x64";
        var latestBuild = await nodeBuildService.GetLatestForPlatformAsync(platform, ct);
        if (latestBuild is not null && NodeVersionComparer.IsNewer(latestBuild.Version, request.Version))
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            updateAvailable = new NodeUpdateInfoDto(
                latestBuild.Version, $"{baseUrl}/api/nodes/download/{latestBuild.Id}",
                latestBuild.Sha256, latestBuild.SizeBytes);
        }
    }

    return Results.Json(new NodeHeartbeatResponse(IntervalSeconds: 30, updateAvailable));
});

// Recorder-node auto-update download — same nodesApi group as everything else here, so it's
// Bearer-authenticated by NodeAuthMiddleware exactly like the rest of /api/nodes/*. Any authenticated
// node can download any build (not scoped to the requesting node's own platform the way dploid's
// agent-download endpoint is) since the buildId a node was ever handed already came from the
// heartbeat handler's own platform-matched lookup above — there's nothing to re-validate here.
nodesApi.MapGet("/download/{buildId:guid}", async (Guid buildId, INodeBuildService nodeBuildService, CancellationToken ct) =>
{
    var build = await nodeBuildService.GetDownloadInfoAsync(buildId, ct);
    if (build is null || !File.Exists(build.FilePath)) return Results.NotFound();
    return Results.File(build.FilePath, "application/octet-stream", "NidusVMS.Node.exe");
});

nodesApi.MapGet("/config", async (HttpContext ctx, INodeService nodeService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    return Results.Json(await nodeService.GetConfigAsync(node.Id, ct));
});

nodesApi.MapPost("/segments", async (HttpContext ctx, List<SegmentReportItem> segments, INodeService nodeService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await nodeService.RecordSegmentsAsync(node.Id, segments, ct);
    return Results.Ok();
});

// Feeds StorageManager's reconciliation sweep (hourly, not every 5-minute eviction cycle) — the
// node diffs this against what's actually on its own disk to find rows a past failed deletion
// report orphaned, and reports them through the same /segments/delete path above.
nodesApi.MapGet("/segments/paths", async (HttpContext ctx, INodeService nodeService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    return Results.Json(await nodeService.ListSegmentFilePathsAsync(node.Id, ct));
});

nodesApi.MapPost("/segments/delete", async (HttpContext ctx, SegmentDeleteRequest request, INodeService nodeService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await nodeService.DeleteSegmentsAsync(node.Id, request.FilePaths, ct);
    return Results.Ok();
});

nodesApi.MapPost("/streams/info", async (HttpContext ctx, List<StreamInfoReportItem> items, INodeService nodeService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await nodeService.UpdateStreamInfoAsync(node.Id, items, ct);
    return Results.Ok();
});

nodesApi.MapPost("/motion-spans", async (HttpContext ctx, List<MotionSpanReportItem> spans, INodeService nodeService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await nodeService.RecordMotionSpansAsync(node.Id, spans, ct);
    return Results.Ok();
});

// M8 pass 6: raw ONVIF PullPoint notifications — separate from /motion-spans since a motion-
// classified event lands in both (this table for the full log, MotionSpans via its own report call
// for the timeline/gating pipeline), not one-or-the-other.
nodesApi.MapPost("/events", async (HttpContext ctx, List<CameraEventReportItem> events, INodeService nodeService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await nodeService.RecordCameraEventsAsync(node.Id, events, ct);
    return Results.Ok();
});

// One export item finishing (success or failure) — ExportRunner reports here once its ffmpeg
// concat exits. Scoped to the reporting node inside ApplyCompletionReportAsync itself, same as
// DeleteSegmentsAsync/UpdateStreamInfoAsync above.
nodesApi.MapPost("/exports/complete", async (HttpContext ctx, List<ExportCompleteReportItem> items, IExportService exportService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await exportService.ApplyCompletionReportAsync(node.Id, items, ct);
    return Results.Ok();
});

// ── Live view media proxy (M5) ───────────────────────────────────────────────
// The browser only ever talks to this host, over the cert that already works — never directly to a
// node (see the plan's "Media path" section for why: nodes never need their own TLS certificate this
// way). Every byte is relayed here: browser <-> this proxy <-> node, over a second WebSocket this
// process opens outbound to the node's own Kestrel port on the LAN (plain HTTP — the short-lived
// signed token below is what keeps that port from being wide open to anything else on the LAN that
// knows the URL shape).
app.MapGet("/live/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, ICameraService cameraService, CancellationToken ct) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var camera = await cameraService.GetAsync(cameraId, ct);
    if (camera?.Node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("This camera's node hasn't reported live-view readiness yet " +
            "(needs at least one heartbeat since being upgraded to a build with live view).");
        return;
    }

    var token = MediaToken.Issue(cameraId, key, TimeSpan.FromSeconds(60));
    var nodeUri = new Uri($"ws://{ip}:{port}/live/{cameraId}?token={Uri.EscapeDataString(token)}");

    using var nodeSocket = new ClientWebSocket();
    try
    {
        await nodeSocket.ConnectAsync(nodeUri, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync($"Could not reach recorder node: {ex.Message}");
        return;
    }

    using var browserSocket = await ctx.WebSockets.AcceptWebSocketAsync();
    await ProxyLiveViewAsync(nodeSocket, browserSocket, ct);
}).RequireAuthorization("Cameras.View");

// M8/M5: one-shot still frame — the zone editor's background image, same proxy shape as /live
// above but a plain HTTP GET instead of a WebSocket (mirrors /playback-segment's shape more than
// /live's, but reuses /live's token family since this authorizes a viewer for the camera's media
// in general, not one specific file). Cameras.Edit, not .View — capturing a frame on demand opens
// a real (if short) RTSP session against the camera, which is a configuration-adjacent action
// (only the zone editor calls this in M8 pass 1), not a passive view.
app.MapGet("/api/cameras/{cameraId:guid}/snapshot", async (Guid cameraId, ICameraService cameraService, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var camera = await cameraService.GetAsync(cameraId, ct);
    if (camera?.Node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
    {
        return Results.Problem(
            "This camera's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var token = MediaToken.Issue(cameraId, key, TimeSpan.FromSeconds(60));
    var nodeUri = $"http://{ip}:{port}/snapshot/{cameraId}?token={Uri.EscapeDataString(token)}";

    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(15); // the node's own grab already bounds itself at 10s
    HttpResponseMessage nodeResponse;
    try
    {
        nodeResponse = await client.GetAsync(nodeUri, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("Timed out waiting for the recorder node to capture a frame.",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not reach recorder node: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }

    if (!nodeResponse.IsSuccessStatusCode) return Results.StatusCode((int)nodeResponse.StatusCode);

    return Results.Stream(await nodeResponse.Content.ReadAsStreamAsync(ct), "image/jpeg");
}).RequireAuthorization("Cameras.Edit");

// ── Zones (M8) ────────────────────────────────────────────────────────────────
// Cameras.Edit throughout — drawing/editing zones is camera configuration, same gate as the
// snapshot endpoint above that feeds the editor's background image.
var zonesApi = app.MapGroup("/api/cameras/{cameraId:guid}/zones").RequireAuthorization("Cameras.Edit");

zonesApi.MapGet("", async (Guid cameraId, IZoneService zones, CancellationToken ct) =>
    Results.Json(await zones.ListAsync(cameraId, ct)));

zonesApi.MapPost("", async (Guid cameraId, SaveZoneRequest request, IZoneService zones, CancellationToken ct) =>
{
    if (!Enum.TryParse<ZoneKind>(request.Kind, out var kind)) return Results.BadRequest("Invalid zone kind.");
    var zone = await zones.CreateAsync(cameraId, request.Name, kind, request.PolygonJson, request.Sensitivity, ct);
    return Results.Json(zone);
});

// Not nested under {cameraId} — an update/delete only needs the zone's own id, and nesting it would
// just be one more value the client has to keep in sync with no actual authorization benefit (the
// zone's real owning camera is read from the row itself, not trusted from the URL).
var zoneApi = app.MapGroup("/api/zones/{id:guid}").RequireAuthorization("Cameras.Edit");

zoneApi.MapPut("", async (Guid id, SaveZoneRequest request, IZoneService zones, CancellationToken ct) =>
{
    if (!Enum.TryParse<ZoneKind>(request.Kind, out var kind)) return Results.BadRequest("Invalid zone kind.");
    await zones.UpdateAsync(id, request.Name, kind, request.PolygonJson, request.Sensitivity, request.IsEnabled, ct);
    return Results.Ok();
});

zoneApi.MapDelete("", async (Guid id, IZoneService zones, CancellationToken ct) =>
{
    await zones.DeleteAsync(id, ct);
    return Results.Ok();
});

// ── Event tag rules (M8 pass 8) ──────────────────────────────────────────────
// Same shape as Zones above: nested-under-camera for list/create (needs the camera to attach to and
// to scope observed-topics), flat by-id for update/delete. Cameras.Edit throughout — configuring
// which ONVIF topics drive recording/tagging is camera configuration, same as drawing a zone.
var eventTagRulesApi = app.MapGroup("/api/cameras/{cameraId:guid}/event-tag-rules").RequireAuthorization("Cameras.Edit");

eventTagRulesApi.MapGet("", async (Guid cameraId, IEventTagRuleService rules, CancellationToken ct) =>
    Results.Json((await rules.ListAsync(cameraId, ct)).Select(r => new EventTagRuleDto(
        r.Id, r.CameraId, r.Name, r.StartTopic, r.StopTopic, r.ColorHex, r.DrivesRecording, r.IsEnabled))));

eventTagRulesApi.MapPost("", async (Guid cameraId, SaveEventTagRuleRequest request, IEventTagRuleService rules, CancellationToken ct) =>
{
    var rule = await rules.CreateAsync(cameraId, request.Name, request.StartTopic, request.StopTopic,
        request.ColorHex, request.DrivesRecording, ct);
    return Results.Json(new EventTagRuleDto(rule.Id, rule.CameraId, rule.Name, rule.StartTopic, rule.StopTopic,
        rule.ColorHex, rule.DrivesRecording, rule.IsEnabled));
});

eventTagRulesApi.MapGet("/observed-topics", async (Guid cameraId, IEventTagRuleService rules, CancellationToken ct) =>
    Results.Json(await rules.ListObservedTopicsAsync(cameraId, ct)));

// Not nested under {cameraId} — same reasoning as the flat /api/zones/{id} group: an update/delete
// only needs the rule's own id, its owning camera is read from the row itself.
var eventTagRuleApi = app.MapGroup("/api/event-tag-rules/{id:guid}").RequireAuthorization("Cameras.Edit");

eventTagRuleApi.MapPut("", async (Guid id, SaveEventTagRuleRequest request, IEventTagRuleService rules, CancellationToken ct) =>
{
    await rules.UpdateAsync(id, request.Name, request.StartTopic, request.StopTopic,
        request.ColorHex, request.DrivesRecording, request.IsEnabled, ct);
    return Results.Ok();
});

eventTagRuleApi.MapDelete("", async (Guid id, IEventTagRuleService rules, CancellationToken ct) =>
{
    await rules.DeleteAsync(id, ct);
    return Results.Ok();
});

// ── Playback & timeline (M7) ─────────────────────────────────────────────────
// GetBucketsAsync/GetSegmentsAsync are plain DB reads (no node involved). /playback-segment
// mirrors /live's proxy shape above — the browser never talks to a node directly, this process
// relays the bytes — but over a plain HTTP GET instead of a WebSocket, and authorizes exactly one
// segment file per request via MediaToken.IssueForSegment rather than a whole camera's live feed.
var playbackApi = app.MapGroup("/api/cameras/{cameraId:guid}").RequireAuthorization("Playback.View");

playbackApi.MapGet("/timeline", async (Guid cameraId, DateTime from, DateTime to, int? buckets, ITimelineService timeline, CancellationToken ct) =>
    Results.Json(await timeline.GetBucketsAsync(cameraId, from, to, buckets ?? 200, ct)));

playbackApi.MapGet("/segments", async (Guid cameraId, DateTime from, DateTime to, ITimelineService timeline, CancellationToken ct) =>
    Results.Json(await timeline.GetSegmentsAsync(cameraId, from, to, ct)));

// Merged across the given cameraIds (repeated query param) — the "was anything recording in this
// view" overview timeline on Pages/Playback, separate from the per-camera one above. cameraIds
// omitted falls back to every camera (see GetGlobalBucketsAsync's own doc comment) — playback-
// player.js always passes the current view's own camera set, so in practice this stays scoped to
// what's actually on screen rather than the whole system.
app.MapGet("/api/timeline", async (DateTime from, DateTime to, int? buckets, Guid[]? cameraIds, ITimelineService timeline, CancellationToken ct) =>
    Results.Json(await timeline.GetGlobalBucketsAsync(from, to, buckets ?? 200, cameraIds, ct))
).RequireAuthorization("Playback.View");

// M8: Live-view motion indicator's signal — polled periodically by live-view.js, not pushed. Gated
// Cameras.View (not Playback.View) since it's a Live-page concern, matching /live's own gate.
app.MapGet("/api/cameras/motion-state", async (ITimelineService timeline, CancellationToken ct) =>
    Results.Json(await timeline.GetCamerasWithActiveMotionAsync(ct))
).RequireAuthorization("Cameras.View");

app.MapGet("/playback-segment/{cameraId:guid}/{segmentId:long}", async (
    Guid cameraId, long segmentId, ITimelineService timeline, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var segment = await timeline.GetSegmentForPlaybackAsync(cameraId, segmentId, ct);
    if (segment is null) return Results.NotFound();
    if (segment.NodeIp is null || segment.NodeLivePort is null || segment.NodeMediaSigningKey is null)
    {
        return Results.Problem(
            "This segment's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var token = MediaToken.IssueForSegment(cameraId, segment.FilePath, segment.NodeMediaSigningKey, TimeSpan.FromSeconds(30));
    var nodeUri = $"http://{segment.NodeIp}:{segment.NodeLivePort}/playback-segment/{cameraId}" +
        $"?path={Uri.EscapeDataString(segment.FilePath)}&token={Uri.EscapeDataString(token)}";

    // Shorter than HttpClient's 100s default — a genuinely stuck node/storage read (confirmed
    // possible: a slow SMB share) shouldn't be able to hold this request open for nearly two
    // minutes with the browser just showing "Loading…" the whole time.
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(25);
    HttpResponseMessage nodeResponse;
    try
    {
        nodeResponse = await client.GetAsync(nodeUri, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("Timed out waiting for the recorder node to start responding (its storage may be slow or unreachable).",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not reach recorder node: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }

    if (!nodeResponse.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)nodeResponse.StatusCode);
    }

    return Results.Stream(await nodeResponse.Content.ReadAsStreamAsync(ct), "video/mp4");
}).RequireAuthorization("Playback.View");

// ── Multi-camera export ──────────────────────────────────────────────────────
// Trigger from Playback's toolbar. Async: creates the job/items and returns immediately —
// ExportJobDispatcher (a BackgroundService) picks up Queued items on its own poll cycle and does
// the actual dispatch/ffmpeg work against each camera's node. Gated the same Exports.View
// permission as the Exports page itself, rather than a separate Exports.Edit — the whole feature is
// meant to be usable by anyone who can see the results, with no distinct "can trigger but can't
// view" or "can view but can't trigger" role split called for.
app.MapPost("/api/exports", async (HttpContext ctx, CreateExportRequest request, IExportService exportService, CancellationToken ct) =>
{
    if (request.CameraIds.Count == 0) return Results.BadRequest("Select at least one camera.");
    if (request.ToUtc <= request.FromUtc) return Results.BadRequest("End time must be after start time.");

    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    var userName = ctx.User.Identity?.Name;
    var job = await exportService.CreateJobAsync(request.CameraIds, request.FromUtc, request.ToUtc, userId, userName, ct);
    return Results.Json(new { jobId = job.Id });
}).RequireAuthorization("Exports.View");

// Download proxy — same "browser only ever talks to this host, IIS relays every byte" shape as
// /playback-segment above, but with Content-Disposition: attachment (via Results.Stream's
// fileDownloadName), since this is the first proxy route meant to be saved rather than played
// inline through MSE.
app.MapGet("/export-download/{exportItemId:guid}", async (
    Guid exportItemId, IExportService exportService, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var info = await exportService.GetDownloadInfoAsync(exportItemId, ct);
    if (info is null) return Results.NotFound();
    if (info.NodeIp is null || info.NodeLivePort is null || info.NodeMediaSigningKey is null)
    {
        return Results.Problem(
            "This export's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var token = MediaToken.IssueForExportDownload(exportItemId, info.FilePath, info.NodeMediaSigningKey, TimeSpan.FromSeconds(30));
    var nodeUri = $"http://{info.NodeIp}:{info.NodeLivePort}/export-file/{exportItemId}" +
        $"?path={Uri.EscapeDataString(info.FilePath)}&token={Uri.EscapeDataString(token)}";

    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(25);
    HttpResponseMessage nodeResponse;
    try
    {
        nodeResponse = await client.GetAsync(nodeUri, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("Timed out waiting for the recorder node to start responding (its storage may be slow or unreachable).",
            statusCode: StatusCodes.Status504GatewayTimeout);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not reach recorder node: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }

    if (!nodeResponse.IsSuccessStatusCode)
    {
        return Results.StatusCode((int)nodeResponse.StatusCode);
    }

    var fileName = Path.GetFileName(info.FilePath);
    return Results.Stream(await nodeResponse.Content.ReadAsStreamAsync(ct), "video/mp4", fileDownloadName: fileName);
}).RequireAuthorization("Exports.View");

app.Run();

// One-directional relay (node -> browser) frame by frame, not message by message — a fragment
// larger than the read buffer arrives across multiple ReceiveAsync calls with EndOfMessage=false
// until the last one, and forwarding each frame as received (with whatever EndOfMessage it carried)
// reassembles correctly on the browser side without this proxy ever needing to buffer a whole message.
static async Task ProxyLiveViewAsync(WebSocket node, WebSocket browser, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    try
    {
        while (node.State == WebSocketState.Open && browser.State == WebSocketState.Open)
        {
            var result = await node.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            await browser.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
    finally
    {
        if (browser.State == WebSocketState.Open)
        {
            try { await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
        }
        if (node.State == WebSocketState.Open)
        {
            try { await node.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
        }
    }
}

void EnsureWritableDirectory(string path)
{
    try
    {
        Directory.CreateDirectory(path);
        var test = Path.Combine(path, ".startup-write-test");
        File.WriteAllText(test, DateTime.UtcNow.ToString("o"));
        File.Delete(test);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
    {
        throw new InvalidOperationException(
            $"Unable to initialize data protection key folder at '{path}'. " +
            "Ensure the application identity has write permission to this directory.", ex);
    }
}
