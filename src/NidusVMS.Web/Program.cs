using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NidusVMS.Core.Dtos;
using NidusVMS.Core.Entities;
using NidusVMS.Core.Interfaces;
using NidusVMS.Core.Security;
using NidusVMS.Infrastructure.Auth;
using NidusVMS.Infrastructure.Data;
using NidusVMS.Infrastructure.Repositories;
using NidusVMS.Infrastructure.Security;
using NidusVMS.Infrastructure.Services;
using NidusVMS.Web.Health;
using NidusVMS.Web.Middleware;

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
builder.Services.AddScoped<IViewService, ViewService>();
builder.Services.AddScoped<ITimelineService, TimelineService>();

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

nodesApi.MapPost("/heartbeat", async (HttpContext ctx, NodeHeartbeatRequest request, INodeService nodeService, CancellationToken ct) =>
{
    // LastSeenAt/Status/LastIpAddress are already updated by NodeAuthMiddleware's AuthenticateAsync
    // call for every authenticated request. Version/LivePort can only be updated here, not in the
    // middleware — middleware runs before this handler's request body is bound, so it has nothing
    // reported to stamp; this is the one place NodeHeartbeatRequest's fields are actually read.
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await nodeService.RecordHeartbeatAsync(node.Id, request.FreeBytes, request.TotalBytes, request.Version, request.LivePort, ct);
    return Results.Json(new NodeHeartbeatResponse(IntervalSeconds: 30));
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

// Merged across every camera, not scoped to one — the "was anything recording anywhere" overview
// timeline on Pages/Playback, separate from the per-camera one above.
app.MapGet("/api/timeline", async (DateTime from, DateTime to, int? buckets, ITimelineService timeline, CancellationToken ct) =>
    Results.Json(await timeline.GetGlobalBucketsAsync(from, to, buckets ?? 200, ct))
).RequireAuthorization("Playback.View");

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
