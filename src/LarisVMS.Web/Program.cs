using System.Net.WebSockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Logging;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Auth;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Repositories;
using LarisVMS.Infrastructure.Security;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Health;
using LarisVMS.Web.Helpers;
using LarisVMS.Web.Middleware;
using LarisVMS.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Log capture (M11) ────────────────────────────────────────────────────────
// Generic Host's default provider is console-only — invisible once the process isn't attached to a
// terminal (IIS's own AspNetCoreModule redirect (web.config's stdoutLogFile) only captures raw
// Console output and ASP.NET Core Module's own diagnostics, not structured ILogger lines). Writes
// into the same ".\logs" directory IIS already proves writable by running under this exact app pool
// identity, with an "app-" prefix so its daily files never collide with IIS's own "stdout_*" ones.
// LogsRetentionService (registered below) sweeps files older than its retention window.
builder.Logging.AddProvider(new FileLoggerProvider(
    LogPaths.AppLogsDirectory(builder.Configuration), "app", LogLevel.Information));

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
// Deliberately still "Rcordr" — never renamed to "NidusVMS" and not renamed to "LarisVMS" either:
// this string is the key-ring isolation discriminator, not a display name — it's baked into every
// existing encrypted value (camera/SMB credentials, node media signing keys), and changing it makes
// all of that permanently undecryptable (confirmed live: CryptographicException "The payload was
// invalid" the moment this got swept by the project's earlier Rcordr->NidusVMS rename along with
// everything else). Applies equally to the current NidusVMS->LarisVMS rename. No user ever sees this
// string. If it's ever changed, every already-encrypted value needs re-encrypting first — not a
// find-and-replace.
var dataProtectionPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "keys");
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
    // The real per-user session cutoff is now the per-role setting enforced in OnValidatePrincipal
    // below, not this value — this is only the outer ceiling the cookie itself will never exceed
    // regardless of what any role is configured for, generous enough that "0 = never expire" on
    // every one of a user's roles behaves as advertised rather than silently capping out here.
    // SlidingExpiration still renews it on activity, same as before.
    options.ExpireTimeSpan = TimeSpan.FromDays(397); // just past a year — IIS/browser cookie norms
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

    // ── Per-role session lifetime (M14) ─────────────────────────────────────
    // AddIdentity() already registered its own OnValidatePrincipal (SecurityStampValidator —
    // re-validates the ticket periodically and signs a user out everywhere their password/security
    // stamp changes). Configure<CookieAuthenticationOptions> delegates apply in registration order
    // against the *same* Events instance, so naively assigning options.Events.OnValidatePrincipal
    // here would silently replace Identity's own handler rather than add to it — captured first and
    // chained instead, so both run. RejectPrincipal() sets ctx.Principal back to null, which is what
    // lets this check for "did the security-stamp check already reject this ticket" after calling it.
    var previousValidatePrincipal = options.Events.OnValidatePrincipal;
    options.Events.OnValidatePrincipal = async ctx =>
    {
        if (previousValidatePrincipal is not null) await previousValidatePrincipal(ctx);
        if (ctx.Principal is null) return; // already rejected by the security-stamp check above

        var issuedUtc = ctx.Properties.IssuedUtc;
        if (issuedUtc is null) return; // no issued time to measure age against — fail open, not closed

        var roleNames = ctx.Principal.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value).ToList();
        if (roleNames.Count == 0) return;

        var db = ctx.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var settings = ctx.HttpContext.RequestServices.GetRequiredService<ISettingsResolver>();

        var roleIds = await db.Roles.Where(r => roleNames.Contains(r.Name!)).Select(r => r.Id).ToListAsync();
        var hoursPerRole = new List<int>();
        foreach (var roleId in roleIds)
            hoursPerRole.Add(await settings.GetAsync(SessionLifetimePolicy.SettingKey(roleId), SessionLifetimePolicy.DefaultHours));

        var window = SessionLifetimePolicy.EffectiveWindow(hoursPerRole);
        if (!SessionLifetimePolicy.HasExpired(issuedUtc.Value, DateTimeOffset.UtcNow, window)) return;

        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
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
builder.Services.AddScoped<IUserPreferenceService, UserPreferenceService>();
builder.Services.AddScoped<ICameraAccessService, CameraAccessService>();
builder.Services.AddScoped<ICameraDiscoveryService, CameraDiscoveryService>();
builder.Services.AddScoped<ICameraService, CameraService>();
builder.Services.AddScoped<ICameraGroupService, CameraGroupService>();
builder.Services.AddScoped<INodeService, NodeService>();
builder.Services.AddScoped<INodeBuildService, NodeBuildService>();
builder.Services.AddScoped<IViewService, ViewService>();
builder.Services.AddScoped<ITimelineService, TimelineService>();
builder.Services.AddScoped<IZoneService, ZoneService>();
builder.Services.AddScoped<IEventTagRuleService, EventTagRuleService>();
builder.Services.AddScoped<IScheduleWindowService, ScheduleWindowService>();
builder.Services.AddScoped<IExportService, ExportService>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IBrandingService, BrandingService>();
builder.Services.AddScoped<IEventColorService, EventColorService>();

// First Web-tier BackgroundService — see ExportJobDispatcher's own doc comment for why exports
// needed one instead of a synchronous per-camera download.
builder.Services.AddHostedService<ExportJobDispatcher>();
builder.Services.AddHostedService<BackupHostedService>();
builder.Services.AddHostedService<LogsRetentionService>();
builder.Services.AddHostedService<AuditLogRetentionService>();
builder.Services.AddHostedService<CameraReprobeService>();

// ── ONVIF HTTP client ────────────────────────────────────────────────────────
// CameraService takes a Func<HttpClient> rather than IHttpClientFactory directly so
// LarisVMS.Infrastructure doesn't need a package reference just for the factory interface — Web
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
// After the setup gate, not before: pre-setup there may be no Settings table to read yet, and every
// request pre-setup is already confined to the wizard's own exempt paths anyway.
app.UsePortSegmentation();

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
    await nodeService.RecordHeartbeatAsync(node.Id, request.FreeBytes, request.TotalBytes, request.Version, request.LivePort,
        request.SentAtUtc, DateTime.UtcNow, ct);

    // ── Auto-update check ────────────────────────────────────────────────
    // Global-only gate (Admin/Settings/Nodes' NodeAutoUpdate.Enabled) — no per-node override for this
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
    return Results.File(build.FilePath, "application/octet-stream", "LarisVMS.Node.exe");
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
app.MapGet("/live/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, ICameraService cameraService,
    IAuditService auditService, CancellationToken ct) =>
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

    // Logged here — after the camera resolves, before the socket is accepted — rather than per page
    // load: this endpoint is opened once per camera actually watched, and (since the flat all-cameras
    // grid was removed in 0.80.0) it is the single chokepoint every live-viewing path in the app goes
    // through. A reconnect after a network blip does log a second entry; that's deliberate, an audit
    // trail should show each time the stream was actually opened rather than hide it behind session
    // bookkeeping.
    await auditService.LogAsync("Camera.View",
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
        ctx.User.Identity?.Name, ctx.Connection.RemoteIpAddress?.ToString(), camera.Name, ct);

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

// ── Schedule windows (M8 Schedule mode) ──────────────────────────────────────
// Same shape as Zones/Event tag rules above: nested-under-camera for list/create, flat by-id for
// update/delete. Cameras.Edit throughout — same reasoning as those two.
var scheduleWindowsApi = app.MapGroup("/api/cameras/{cameraId:guid}/schedule-windows").RequireAuthorization("Cameras.Edit");

scheduleWindowsApi.MapGet("", async (Guid cameraId, IScheduleWindowService windows, CancellationToken ct) =>
    Results.Json(await windows.ListAsync(cameraId, ct)));

scheduleWindowsApi.MapPost("", async (Guid cameraId, SaveScheduleWindowRequest request, IScheduleWindowService windows, CancellationToken ct) =>
{
    if (!Enum.TryParse<DayOfWeekFlags>(request.Days, ignoreCase: true, out var days)) return Results.BadRequest("Invalid day flags.");
    var window = await windows.CreateAsync(cameraId, days, request.StartTime, request.EndTime, ct);
    return Results.Json(window);
});

// Not nested under {cameraId} — same reasoning as the flat /api/zones/{id} group.
var scheduleWindowApi = app.MapGroup("/api/schedule-windows/{id:guid}").RequireAuthorization("Cameras.Edit");

scheduleWindowApi.MapPut("", async (Guid id, SaveScheduleWindowRequest request, IScheduleWindowService windows, CancellationToken ct) =>
{
    if (!Enum.TryParse<DayOfWeekFlags>(request.Days, ignoreCase: true, out var days)) return Results.BadRequest("Invalid day flags.");
    await windows.UpdateAsync(id, days, request.StartTime, request.EndTime, request.IsEnabled, ct);
    return Results.Ok();
});

scheduleWindowApi.MapDelete("", async (Guid id, IScheduleWindowService windows, CancellationToken ct) =>
{
    await windows.DeleteAsync(id, ct);
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

// Audit-only: Pages/Playback resolves which view (and therefore which cameras) is being reviewed
// entirely client-side, so there is no existing server hit that knows "this user just started
// reviewing these cameras" — /playback-segment fires continuously per segment loaded, far too
// granular to be a meaningful audit record. playback-player.js posts here once per view selection;
// the response is ignored client-side, so a failure here can never block playback from starting.
app.MapPost("/api/playback/view-opened", async (HttpContext ctx, ViewOpenedRequest request,
    IViewService viewService, ICameraService cameraService, IAuditService auditService, CancellationToken ct) =>
{
    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    var view = await viewService.GetVisibleToAsync(request.ViewId, userId, ct);
    if (view is null) return Results.NotFound();

    var cameraIds = ViewLayout.CameraIds(view.LayoutJson);
    var cameras = await cameraService.ListAsync(ct);
    var names = cameraIds
        .Select(id => cameras.FirstOrDefault(c => c.Id == id)?.Name ?? id.ToString())
        .ToList();

    await auditService.LogAsync("Playback.View", userId, ctx.User.Identity?.Name,
        ctx.Connection.RemoteIpAddress?.ToString(),
        names.Count == 0
            ? $"View '{view.Name}' (no cameras)"
            : $"View '{view.Name}': {string.Join(", ", names)}",
        ct);
    return Results.NoContent();
}).RequireAuthorization("Playback.View");

// M8: Live-view motion indicator's signal — polled periodically by live-view.js, not pushed. Gated
// Cameras.View (not Playback.View) since it's a Live-page concern, matching /live's own gate.
app.MapGet("/api/cameras/motion-state", async (ITimelineService timeline, CancellationToken ct) =>
    Results.Json(await timeline.GetCamerasWithActiveMotionAsync(ct))
).RequireAuthorization("Cameras.View");

// Companion to motion-state: which cameras are seeing a *classified* object right now
// (person/vehicle/face) rather than just movement. Separate endpoint rather than a wider
// motion-state payload so the existing badge keeps working untouched on any client that hasn't been
// updated, and so a deployment with no object-capable cameras pays nothing for it.
app.MapGet("/api/cameras/detection-state", async (ITimelineService timeline, CancellationToken ct) =>
    Results.Json(await timeline.GetActiveDetectionsAsync(ct))
).RequireAuthorization("Cameras.View");

// The admin-configured palette, for the parts of the UI that draw event colors client-side. Only
// motion and recording are needed here: a detected object's color already rides along on each
// timeline bucket (TimelineService resolves it server-side into TagColorHex), so this covers exactly
// the two the canvas would otherwise have to hardcode. Fetched once per page rather than per
// timeline, and timeline.js keeps its built-in fallback so a failure here just means default colors.
app.MapGet("/api/timeline/colors", async (IEventColorService eventColors, CancellationToken ct) =>
{
    var palette = await eventColors.GetAsync(ct);
    return Results.Json(new { motion = palette.MotionColor, recording = palette.RecordingColor });
}).RequireAuthorization("Cameras.View");

// M11: Pages/Index's own 60s AJAX refresh (dashboard.js) — same IDashboardService.GetHealthAsync
// Pages/Index.cshtml.cs's OnGetAsync itself calls, so the polled data and the server-rendered
// initial page can never independently drift out of sync. Plain [Authorize] (no specific resource
// policy), matching IndexModel's own gate — the dashboard shows a summary, not a resource to scope.
app.MapGet("/api/dashboard", async (IDashboardService dashboardService, CancellationToken ct) =>
    Results.Json(await dashboardService.GetHealthAsync(ct))
).RequireAuthorization();

// ── User preferences (M14) ───────────────────────────────────────────────────
// The server-backed replacement for what used to live only in localStorage — theme, last-watched
// view, table page size, playback clock format. Plain [Authorize] (no specific resource policy):
// every signed-in user reads and writes only their own preferences, identified from their own
// claims, never someone else's — there's nothing here for a permission to scope.
app.MapGet("/api/preferences", async (HttpContext ctx, IUserPreferenceService preferences, CancellationToken ct) =>
{
    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userId is null) return Results.Unauthorized();
    return Results.Json(await preferences.GetAllAsync(userId, ct));
}).RequireAuthorization();

app.MapPut("/api/preferences/{key}", async (string key, HttpContext ctx, SetPreferenceRequest request,
    IUserPreferenceService preferences, CancellationToken ct) =>
{
    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (userId is null) return Results.Unauthorized();
    await preferences.SetAsync(userId, key, request.Value, ct);
    return Results.NoContent();
}).RequireAuthorization();

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

// M7 pass 2: same "browser never talks to a node directly" proxy shape as /playback-segment above,
// but for one extracted JPEG hover-preview frame instead of a whole segment's bytes. Shared by both
// the bucketed/historical lookup (atUtc-based) below and the Dashboard's "most recent" lookup —
// everything past resolving a ThumbnailInfo is identical proxy plumbing (token, node URI, relay).
async Task<IResult> ProxyThumbnailAsync(Guid cameraId, ThumbnailInfo? thumb, IHttpClientFactory httpFactory, CancellationToken ct)
{
    if (thumb is null) return Results.NotFound();
    if (thumb.NodeIp is null || thumb.NodeLivePort is null || thumb.NodeMediaSigningKey is null)
    {
        return Results.Problem(
            "This segment's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var token = MediaToken.IssueForThumbnail(cameraId, thumb.FilePath, thumb.OffsetSeconds, thumb.NodeMediaSigningKey, TimeSpan.FromSeconds(30));
    var nodeUri = $"http://{thumb.NodeIp}:{thumb.NodeLivePort}/playback-thumbnail/{cameraId}" +
        $"?path={Uri.EscapeDataString(thumb.FilePath)}&offset={thumb.OffsetSeconds}&token={Uri.EscapeDataString(token)}";

    // Shorter than /playback-segment's 25s — this relays one small JPEG frame, not a video segment.
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(15);
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

    return Results.Stream(await nodeResponse.Content.ReadAsStreamAsync(ct), "image/jpeg");
}

// GetThumbnailInfoAsync resolves which segment covers atUtc and buckets/clamps the offset within it
// server-side.
app.MapGet("/playback-thumbnail/{cameraId:guid}", async (
    Guid cameraId, DateTime atUtc, ITimelineService timeline, IHttpClientFactory httpFactory, CancellationToken ct) =>
    await ProxyThumbnailAsync(cameraId, await timeline.GetThumbnailInfoAsync(cameraId, atUtc, ct), httpFactory, ct)
).RequireAuthorization("Playback.View");

// Dashboard's "most recent thumbnail" column (Pages/Index, dashboard.js) — plain [Authorize], not
// Playback.View, matching Pages/Index's own gate (IndexModel has no specific resource policy) rather
// than the stricter one the historical/scrub lookup above uses.
app.MapGet("/playback-thumbnail/{cameraId:guid}/latest", async (
    Guid cameraId, ITimelineService timeline, IHttpClientFactory httpFactory, CancellationToken ct) =>
    await ProxyThumbnailAsync(cameraId, await timeline.GetLatestThumbnailInfoAsync(cameraId, ct), httpFactory, ct)
).RequireAuthorization();

// ── Multi-camera export ──────────────────────────────────────────────────────
// Trigger from Playback's toolbar. Async: creates the job/items and returns immediately —
// ExportJobDispatcher (a BackgroundService) picks up Queued items on its own poll cycle and does
// the actual dispatch/ffmpeg work against each camera's node. Gated the same Exports.View
// permission as the Exports page itself, rather than a separate Exports.Edit — the whole feature is
// meant to be usable by anyone who can see the results, with no distinct "can trigger but can't
// view" or "can view but can't trigger" role split called for.
app.MapPost("/api/exports", async (HttpContext ctx, CreateExportRequest request, IExportService exportService,
    IAuditService auditService, ICameraAccessService cameraAccess, CancellationToken ct) =>
{
    if (request.CameraIds.Count == 0) return Results.BadRequest("Select at least one camera.");
    if (request.ToUtc <= request.FromUtc) return Results.BadRequest("End time must be after start time.");

    // Checked here, not only by hiding cameras from the picker that built this request: unlike the
    // playback surfaces above, this creates a real, downloadable file — the most compliance-sensitive
    // step in the whole export flow per this route's own Export.Download audit comment below — so
    // it's worth the one extra check even though a client that only ever sees LarisVMS's own picker
    // could never construct a disallowed request in the first place.
    //
    // Results.Problem with an explicit status rather than Results.Forbid(): under cookie
    // authentication, Forbid()'s default handling can redirect to AccessDeniedPath instead of
    // returning a clean 403 to a fetch() caller, which every other domain-specific rejection on this
    // route already avoids the same way (see the 503/504 Results.Problem calls below).
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Export, ct);
    if (accessible is not null && request.CameraIds.Any(id => !accessible.Contains(id)))
        return Results.Problem("You don't have export access to one or more of the selected cameras.",
            statusCode: StatusCodes.Status403Forbidden);

    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    var userName = ctx.User.Identity?.Name;
    var job = await exportService.CreateJobAsync(request.CameraIds, request.FromUtc, request.ToUtc, userId, userName, ct);
    await auditService.LogAsync("Export.Create", userId, userName, ctx.Connection.RemoteIpAddress?.ToString(),
        $"{request.CameraIds.Count} camera(s), {request.FromUtc:u} - {request.ToUtc:u}", ct);
    return Results.Json(new { jobId = job.Id });
}).RequireAuthorization("Exports.View");

// Download proxy — same "browser only ever talks to this host, IIS relays every byte" shape as
// /playback-segment above, but with Content-Disposition: attachment (via Results.Stream's
// fileDownloadName), since this is the first proxy route meant to be saved rather than played
// inline through MSE.
app.MapGet("/export-download/{exportItemId:guid}", async (
    HttpContext ctx, Guid exportItemId, IExportService exportService, IAuditService auditService,
    IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var info = await exportService.GetDownloadInfoAsync(exportItemId, ct);
    if (info is null) return Results.NotFound();
    if (info.NodeIp is null || info.NodeLivePort is null || info.NodeMediaSigningKey is null)
    {
        return Results.Problem(
            "This export's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Logged once the item resolves and before any bytes move, not after the transfer completes — a
    // download that was started and then abandoned mid-transfer still means footage left the system,
    // which is exactly what this entry exists to record. Of the whole export flow this is the most
    // compliance-sensitive step (completed data exfiltration, not just viewing) and it had no audit
    // coverage at all before.
    await auditService.LogAsync("Export.Download",
        ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
        ctx.User.Identity?.Name, ctx.Connection.RemoteIpAddress?.ToString(),
        Path.GetFileName(info.FilePath), ct);

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

// Exports page's auto-refresh poll — same data ListJobsAsync already feeds the page's initial
// server-render, just as JSON so the page can redraw its own table on an interval without a full
// reload. Deliberately returns every job on every poll rather than a since/delta query: the list is
// small (exports are an occasional, manually-triggered action, not a high-volume feed) and a full
// snapshot means the client-side renderer never has to reconcile a partial update.
app.MapGet("/api/exports", async (IExportService exportService, ICameraService cameraService, INodeService nodeService, CancellationToken ct) =>
{
    var jobs = await exportService.ListJobsAsync(ct);
    var cameraNames = (await cameraService.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
    var nodeNames = (await nodeService.ListAsync(ct)).ToDictionary(n => n.Id, n => n.Name);

    return Results.Json(jobs.Select(j => new
    {
        id = j.Id,
        createdUtc = j.CreatedUtc,
        requestedByUserName = j.RequestedByUserName,
        fromUtc = j.FromUtc,
        toUtc = j.ToUtc,
        status = j.Status.ToString(),
        items = j.Items.Select(i => new
        {
            id = i.Id,
            cameraId = i.CameraId,
            cameraName = cameraNames.GetValueOrDefault(i.CameraId, "(deleted camera)"),
            nodeName = i.NodeId is { } nid ? nodeNames.GetValueOrDefault(nid) : null,
            status = i.Status.ToString(),
            errorMessage = i.ErrorMessage,
            outputSizeBytes = i.OutputSizeBytes
        })
    }));
}).RequireAuthorization("Exports.View");

// Trash button: best-effort tells each Done item's owning node to remove its output file (the
// node's own StorageManager sweep is the 7-day backstop if this doesn't reach it — see
// SweepExportsDirectory), then removes the job/items rows regardless of whether the node calls
// succeeded. Refuses (400) while any item is still Queued/Running — see
// ExportJobDeletionInfo's own doc comment for why.
app.MapDelete("/api/exports/{jobId:guid}", async (
    Guid jobId, IExportService exportService, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    var info = await exportService.GetDeletionInfoAsync(jobId, ct);
    if (info is null) return Results.NotFound();
    if (!info.CanDelete) return Results.BadRequest(info.Reason);

    foreach (var file in info.Files)
    {
        if (file.NodeIp is null || file.NodeLivePort is null || file.NodeMediaSigningKey is null) continue;

        try
        {
            var token = MediaToken.IssueForExportDelete(file.ExportItemId, file.FilePath, file.NodeMediaSigningKey, TimeSpan.FromSeconds(30));
            var nodeUri = $"http://{file.NodeIp}:{file.NodeLivePort}/export-file/{file.ExportItemId}" +
                $"?path={Uri.EscapeDataString(file.FilePath)}&token={Uri.EscapeDataString(token)}";
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            await client.DeleteAsync(nodeUri, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort — the node's own 7-day export-retention sweep cleans this up either way,
            // so an unreachable node here shouldn't block removing the (already-finished) job.
        }
    }

    await exportService.DeleteJobAsync(jobId, ct);
    return Results.Ok();
}).RequireAuthorization("Exports.View");

// Retry button: re-queues one Failed item for ExportJobDispatcher's next poll cycle. Gated the same
// Exports.View permission as everything else on this page — see the /api/exports POST route's own
// comment for why there's no separate Exports.Edit.
app.MapPost("/api/exports/{itemId:guid}/retry", async (Guid itemId, IExportService exportService, CancellationToken ct) =>
{
    var ok = await exportService.RetryItemAsync(itemId, ct);
    return ok ? Results.Ok() : Results.BadRequest("Only a failed item can be retried.");
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
