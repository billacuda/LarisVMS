using System.Globalization;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Core.Logging;
using LarisVMS.Core.Security;
using LarisVMS.Infrastructure.Alerts;
using LarisVMS.Infrastructure.Alerts.Channels;
using LarisVMS.Infrastructure.Auth;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Email;
using LarisVMS.Infrastructure.Email.OAuth;
using LarisVMS.Infrastructure.Email.Providers;
using LarisVMS.Infrastructure.Repositories;
using LarisVMS.Infrastructure.Security;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Onvif.Soap;
using LarisVMS.Web.Auth;
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
        if (SessionLifetimePolicy.HasExpired(issuedUtc.Value, DateTimeOffset.UtcNow, window))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        // Roles/permissions overhaul, pass 5: RoleAssignmentExpirySweepService only runs once a
        // minute — a user whose own session also happened to expire around the same time could
        // otherwise re-log in and have a fresh cookie minted with a role that's already past its
        // ExpiresAtUtc but hasn't been swept yet. This narrows that window from "up to one sweep
        // interval" to "the very next request": a full sign-out rather than surgically dropping just
        // the one expired role claim, matching this handler's own existing all-or-nothing shape above.
        var userId = ctx.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId is not null &&
            await db.RoleAssignmentExpiries.AnyAsync(e => e.UserId == userId && roleIds.Contains(e.RoleId) && e.ExpiresAtUtc <= DateTime.UtcNow))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});

// ── Entra ID sign-in (M20 pass 3) ───────────────────────────────────────────────
// Registered unconditionally — AddIdentity() already made IdentityConstants.ApplicationScheme (the
// cookie) the default authenticate/challenge scheme, so adding this external scheme here doesn't
// change what an ordinary password sign-in does. Real per-request options (Authority/ClientId/
// ClientSecret) come from EntraOidcOptionsConfigurator, not the callback below — left mostly blank
// here since AddOpenIdConnect requires *a* configure delegate even when the real one is supplied via
// IConfigureNamedOptions<T>. Login.cshtml's own override decides whether to actually offer the "Sign
// in with Microsoft" button (EntraSsoSettings.IsEnabled), independent of this registration existing.
builder.Services.AddAuthentication().AddOpenIdConnect(EntraOidcOptionsConfigurator.SchemeName, _ => { });
// Registered as IConfigureOptions<T>, not IConfigureNamedOptions<T> — OptionsFactory<T>'s
// constructor only takes IEnumerable<IConfigureOptions<T>> from DI, and the container resolves by
// the exact registered service type, not by every interface the implementation happens to satisfy.
// Registering under IConfigureNamedOptions<T> (as this line originally did) meant the factory's DI
// resolution never found this configurator at all — Configure() silently never ran, ClientId stayed
// at OpenIdConnectOptions' own null default, and RemoteAuthenticationOptions.Validate() then threw
// on every single request (AuthenticationMiddleware builds every IAuthenticationRequestHandler
// scheme, OIDC included, to check whether it owns the current request path) — confirmed live as an
// unhandled ArgumentNullException on 'ClientId' on every page load, not just a sign-in attempt.
builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, EntraOidcOptionsConfigurator>();

// ── Authorization / RBAC ──────────────────────────────────────────────────────
builder.Services.AddAuthorization(options =>
{
    // "Administrator" renamed to "Super Admin" by the roles/permissions overhaul (see
    // RoleSeedService) — this policy is currently unreferenced anywhere, but kept correct so it
    // doesn't silently break the day something wires it up.
    options.AddPolicy("AdministratorOnly", policy => policy.RequireRole("Super Admin"));
});
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
// Resolves any "{Resource}.{Action}" policy name (e.g. "Cameras.Edit") on the fly, instead of
// requiring every resource × action combination in the RBAC matrix to be individually registered
// with AddPolicy above.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
builder.Services.AddScoped<ISetupService, SetupService>();
builder.Services.AddScoped<IRoleSeedService, RoleSeedService>();
builder.Services.AddScoped<ICameraGroupSeedService, CameraGroupSeedService>();
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
builder.Services.AddSingleton<DetectedObjectCategoryColorCache>();
builder.Services.AddScoped<IZoneService, ZoneService>();
builder.Services.AddScoped<IEventTagRuleService, EventTagRuleService>();
builder.Services.AddScoped<IScheduleWindowService, ScheduleWindowService>();
builder.Services.AddScoped<IExportService, ExportService>();
builder.Services.AddScoped<IBackupService, BackupService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IBrandingService, BrandingService>();
builder.Services.AddScoped<IEventColorService, EventColorService>();
builder.Services.AddScoped<IPtzService, PtzService>();
builder.Services.AddScoped<IBookmarkService, BookmarkService>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();

// Roles/permissions overhaul, pass 4: PTZ priority arbitration. PtzArbitrationService is a
// singleton — one shared in-process hold table per camera, not per-request state (see its own doc
// comment) — while PtzPriorityResolver stays scoped like every other DbContext-backed service here.
builder.Services.AddSingleton<IPtzArbitrationService, PtzArbitrationService>();
builder.Services.AddScoped<IPtzPriorityResolver, PtzPriorityResolver>();

// ── Email (M15 pass 1) ───────────────────────────────────────────────────────
// One IEmailProvider per EmailProviderType, resolved by EmailProviderFactory — ported from rsolva's
// provider-strategy shape. Only Smtp is implemented so far; Graph/Gmail are later M15 passes.
builder.Services.AddScoped<IEmailProvider, SmtpEmailProvider>();
builder.Services.AddScoped<IEmailProvider, GraphEmailProvider>();
builder.Services.AddScoped<IEmailProvider, GmailEmailProvider>();
builder.Services.AddScoped<EmailProviderFactory>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IOAuthConnectProvider, GoogleOAuthConnectProvider>();
builder.Services.AddScoped<OAuthConnectProviderFactory>();

// ── Alerting (M15 pass 4) ─────────────────────────────────────────────────────
builder.Services.AddScoped<IAlertChannelSender, EmailAlertChannelSender>();
builder.Services.AddScoped<IAlertChannelSender, WebhookAlertChannelSender>();
builder.Services.AddScoped<IAlertChannelSender, NtfyAlertChannelSender>();
builder.Services.AddScoped<IAlertChannelSender, PushoverAlertChannelSender>();
builder.Services.AddScoped<IAlertChannelSender, SlackAlertChannelSender>();
builder.Services.AddScoped<IAlertChannelSender, TeamsAlertChannelSender>();
builder.Services.AddScoped<AlertChannelSenderFactory>();

// First Web-tier BackgroundService — see ExportJobDispatcher's own doc comment for why exports
// needed one instead of a synchronous per-camera download.
builder.Services.AddHostedService<ExportJobDispatcher>();
builder.Services.AddHostedService<BackupHostedService>();
builder.Services.AddHostedService<LogsRetentionService>();
builder.Services.AddHostedService<AuditLogRetentionService>();
builder.Services.AddHostedService<CameraReprobeService>();
builder.Services.AddHostedService<AlertEvaluatorService>();
builder.Services.AddHostedService<BookmarkRetentionService>();
builder.Services.AddHostedService<MotionSpanRetentionService>();
builder.Services.AddHostedService<RoleAssignmentExpirySweepService>();

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

// Roles/permissions overhaul: seeds the 8 built-in roles' RoleProfile rows (renaming
// Administrator/Viewer to Super Admin/Guest-Viewer in place along the way) — see RoleSeedService.
// Idempotent, so safe to run on every startup; only runs once the database actually exists, since a
// brand-new deployment reaches this point before the setup wizard has configured a connection
// string at all, and every RoleSeedService query would otherwise throw.
using (var startupScope = app.Services.CreateScope())
{
    var setupService = startupScope.ServiceProvider.GetRequiredService<ISetupService>();
    if (await setupService.IsDatabaseConfiguredAsync())
    {
        var roleSeedService = startupScope.ServiceProvider.GetRequiredService<IRoleSeedService>();
        await roleSeedService.SeedAsync();

        var cameraGroupSeedService = startupScope.ServiceProvider.GetRequiredService<ICameraGroupSeedService>();
        await cameraGroupSeedService.SeedAsync();
    }
}

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
app.UseRegistrationDisabled();
// After the setup gate, not before: pre-setup there may be no Settings table to read yet, and every
// request pre-setup is already confined to the wizard's own exempt paths anyway.
app.UsePortSegmentation();
// M20 pass 2: same "after the setup gate" reasoning — pre-setup there's no admin yet to have
// configured a list, so it would read empty/open regardless.
app.UseIpAllowList();

app.UseRouting();
// Needed before UseAuthorization so the /live WS upgrade request survives the pipeline as a
// WebSocket rather than being treated as a normal HTTP request that happens to ask for an upgrade.
app.UseWebSockets();
app.UseAuthentication();
// Runs after cookie auth but before authorization, mirroring dploid's AgentAuthMiddleware
// placement — it establishes the node principal (via HttpContext.Items, not a ClaimsPrincipal,
// since these endpoints don't carry [Authorize] policies) independently of the cookie scheme.
app.UseMiddleware<NodeAuthMiddleware>();
// M20 pass 1: authenticates /api/v1/* via the "X-Api-Key" header — see its own doc comment for why
// this one *does* build a real ClaimsPrincipal (role claim only, no NameIdentifier) rather than using
// HttpContext.Items the way NodeAuthMiddleware does.
app.UseMiddleware<ApiKeyAuthMiddleware>();
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
        request.SentAtUtc, DateTime.UtcNow, request.DetectedEncoders, request.DetectedAccelerators, ct);

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
            // Vision* stays null (never offered) unless this build actually has a registered Vision
            // Service binary — see NodeBuildVersion.VisionFilePath's own doc comment for why a build
            // can legitimately have none (a package built with -SkipNodeVision, or an older build
            // that predates this column).
            updateAvailable = new NodeUpdateInfoDto(
                latestBuild.Version, $"{baseUrl}/api/nodes/download/{latestBuild.Id}",
                latestBuild.Sha256, latestBuild.SizeBytes,
                latestBuild.VisionFilePath is not null ? $"{baseUrl}/api/nodes/download/{latestBuild.Id}/vision" : null,
                latestBuild.VisionSha256, latestBuild.VisionSizeBytes);
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

// Companion to the download route above, for the optional Vision Service binary a build may carry
// alongside its Node exe — see NodeBuildVersion.VisionFilePath's own doc comment. 404 both when the
// build itself doesn't exist and when it exists but was never given a Vision binary, since a node only
// ever requests this URL when the heartbeat response's own VisionDownloadUrl was non-null in the first
// place — either case means there's genuinely nothing to serve.
nodesApi.MapGet("/download/{buildId:guid}/vision", async (Guid buildId, INodeBuildService nodeBuildService, CancellationToken ct) =>
{
    var build = await nodeBuildService.GetDownloadInfoAsync(buildId, ct);
    if (build?.VisionFilePath is not { } visionPath || !File.Exists(visionPath)) return Results.NotFound();
    return Results.File(visionPath, "application/octet-stream", "LarisVMS.Vision.Service.exe");
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

// Pass 2c: feeds StorageManager's own snapshot-reconciliation sweep, mirroring the shape of
// /segments/paths above — the node diffs this against its own cam-{id}/snapshots/ cache files to
// self-heal any crop image whose owning MotionSpan row has since been deleted independently (by
// MotionSpanRetentionService), not just as a side effect of that segment being evicted.
nodesApi.MapGet("/snapshots/span-ids", async (HttpContext ctx, INodeService nodeService, CancellationToken ct) =>
{
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    return Results.Json(await nodeService.ListMotionSpanIdsAsync(node.Id, ct));
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
    ICameraAccessService cameraAccess, IAuditService auditService, CancellationToken ct) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    // Global RBAC (Cameras.View, below) says this principal can view *some* camera's live feed;
    // CameraAccess narrows that to *this* camera. Previously unchecked here — the camera/view list
    // pages already filter by this same call, but this endpoint (the one that actually streams
    // video) took only the global gate, so a CameraAccess-restricted user could still open any other
    // camera's live feed directly by GUID. null means unrestricted (admin, an All-scope grant, or a
    // principal with zero CameraAccess rows — see GetAccessibleCameraIdsAsync's own doc comment).
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.View, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("You don't have access to this camera.");
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
    // M18: forwarded as-is, not validated against anything here — see NodeConfigResponse.
    // AdaptiveStreamingEnabled's doc comment for why this tier doesn't also gate on the toggle; the
    // node's own /live route already falls back to Main whenever Sub isn't actually available.
    var role = ctx.Request.Query["role"].ToString();
    var roleQuery = string.Equals(role, "sub", StringComparison.OrdinalIgnoreCase) ? "&role=sub" : "";
    var nodeUri = new Uri($"ws://{ip}:{port}/live/{cameraId}?token={Uri.EscapeDataString(token)}{roleQuery}");

    using var nodeSocket = new ClientWebSocket();
    // Also sent as ?token= above for a node that hasn't updated yet — see MediaTokenRequest's own
    // doc comment. The WebSocket upgrade request is still a plain HTTP GET at the point the node
    // reads this, so the header arrives the same way it would on any other request.
    nodeSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
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

// Object detection plan decision 6: live-view box overlay — a separate WS proxy from the video
// stream above, same token family (viewing a camera's boxes is the same authorization boundary as
// viewing its video). Unlike ProxyLiveViewAsync's pure byte relay, this one parses each JSON tick
// and attaches each box's category color before forwarding — Node and Vision Service both have no
// database access at all, so this proxy is the only tier that can.
app.MapGet("/live/{cameraId:guid}/detections", async (HttpContext ctx, Guid cameraId, ICameraService cameraService,
    ICameraAccessService cameraAccess, DetectedObjectCategoryColorCache colorCache, CancellationToken ct) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.View, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("You don't have access to this camera.");
        return;
    }

    var camera = await cameraService.GetAsync(cameraId, ct);
    if (camera?.Node is not { LastIpAddress: { } ip, LivePort: { } port, MediaSigningKey: { } key })
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsync("This camera's node hasn't reported live-view readiness yet.");
        return;
    }

    var token = MediaToken.Issue(cameraId, key, TimeSpan.FromSeconds(60));
    var nodeUri = new Uri($"ws://{ip}:{port}/live/{cameraId}/detections?token={Uri.EscapeDataString(token)}");

    using var nodeSocket = new ClientWebSocket();
    nodeSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
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
    await ProxyDetectionOverlayAsync(nodeSocket, browserSocket, colorCache, ct);
}).RequireAuthorization("Cameras.View");

// M8/M5: one-shot still frame — the zone editor's background image, same proxy shape as /live
// above but a plain HTTP GET instead of a WebSocket (mirrors /playback-segment's shape more than
// /live's, but reuses /live's token family since this authorizes a viewer for the camera's media
// in general, not one specific file). Cameras.Edit, not .View — capturing a frame on demand opens
// a real (if short) RTSP session against the camera, which is a configuration-adjacent action
// (only the zone editor calls this in M8 pass 1), not a passive view.
app.MapGet("/api/cameras/{cameraId:guid}/snapshot", async (HttpContext ctx, Guid cameraId, ICameraService cameraService,
    ICameraAccessService cameraAccess, IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    // Configure, not View/Playback — this is the zone editor's own configuration-adjacent capture
    // (see this endpoint's own Cameras.Edit gate below), the same category CameraAccessActions.Ptz
    // narrows Cameras.View by for the PTZ endpoints above. Previously relied on the global gate alone.
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Configure, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have configuration access to this camera.", statusCode: StatusCodes.Status403Forbidden);

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
        // See MediaTokenRequest's own doc comment: also sent as ?token= above for a node that
        // hasn't updated yet.
        nodeResponse = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Get, nodeUri, token),
            HttpCompletionOption.ResponseHeadersRead, ct);
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

// ── PTZ (M18 "basic PTZ") ────────────────────────────────────────────────────
// Cameras.View, not .Edit — these controls appear on the Live page itself (same gate the page's own
// tiles already need), narrowed per-camera by CameraAccessActions.Ptz exactly the way the export
// trigger above narrows Exports.View by CameraAccessActions.Export. Called directly against the
// camera's own ONVIF PTZ service (IPtzService, using the same "onvif" named HttpClient probing
// already uses) — no node hop, since a PTZ command is a quick request/response, not a long-lived
// session the way live view or event polling are.
var ptzApi = app.MapGroup("/api/cameras/{cameraId:guid}/ptz").RequireAuthorization("Cameras.View");

// Roles/permissions overhaul, pass 4: after the existing CameraAccessActions.Ptz check, a requester
// whose held roles carry a PTZ priority (RoleProfile.PtzPriorityLevel) is now subject to priority
// arbitration on top of it — a higher-priority holder locks the camera against lower/equal-priority
// commands until their own lockout window elapses. A requester with no priority at all (every custom
// role by default, plus Investigator/Auditor, Guest/Viewer, API/Integration) skips arbitration
// entirely and keeps today's unrestricted first-come behavior — the deliberate migration-safety
// property that leaves every pre-existing deployment's PTZ behavior unchanged until an admin opts a
// role in.
ptzApi.MapPost("/move", async (HttpContext ctx, Guid cameraId, PtzMoveRequest request,
    IPtzService ptzService, ICameraAccessService cameraAccess, IPtzArbitrationService arbitration,
    IPtzPriorityResolver priorityResolver, CancellationToken ct) =>
{
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Ptz, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have PTZ access to this camera.", statusCode: StatusCodes.Status403Forbidden);

    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    var priority = await priorityResolver.GetPtzPriorityAsync(ctx.User, ct);
    var now = DateTime.UtcNow;

    if (priority is { } p)
    {
        var decision = arbitration.TryAcquire(cameraId, userId, p.PriorityLevel, now);
        if (!decision.Granted)
            return Results.Problem("Another operator with higher PTZ priority currently controls this camera.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = decision.RetryAfterSeconds });
    }

    try
    {
        var moved = await ptzService.MoveAsync(cameraId, request.PanX, request.TiltY, request.ZoomX, ct);
        if (!moved)
            return Results.Problem("This camera doesn't support PTZ, or hasn't been probed since PTZ support was added.",
                statusCode: StatusCodes.Status400BadRequest);

        // Only on an actual successful move, never on a mere view — matches "viewing does not reset
        // the timer."
        if (priority is { } granted) arbitration.RecordCommand(cameraId, userId, granted.PriorityLevel, granted.LockoutSeconds, now);
        return Results.Ok();
    }
    catch (OnvifFaultException ex)
    {
        return Results.Problem($"Camera rejected the PTZ command: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not reach camera: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

// Same arbitration check as /move (an unrelated lower-priority stray stop must not interrupt an
// active higher-priority hold) but never calls RecordCommand or releases the hold — lockout is
// strictly time-based, never released early by stopping.
ptzApi.MapPost("/stop", async (HttpContext ctx, Guid cameraId,
    IPtzService ptzService, ICameraAccessService cameraAccess, IPtzArbitrationService arbitration,
    IPtzPriorityResolver priorityResolver, CancellationToken ct) =>
{
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Ptz, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have PTZ access to this camera.", statusCode: StatusCodes.Status403Forbidden);

    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    var priority = await priorityResolver.GetPtzPriorityAsync(ctx.User, ct);
    if (priority is { } p)
    {
        var decision = arbitration.TryAcquire(cameraId, userId, p.PriorityLevel, DateTime.UtcNow);
        if (!decision.Granted)
            return Results.Problem("Another operator with higher PTZ priority currently controls this camera.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = decision.RetryAfterSeconds });
    }

    try
    {
        await ptzService.StopAsync(cameraId, ct);
        return Results.Ok();
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem($"Could not reach camera: {ex.Message}", statusCode: StatusCodes.Status502BadGateway);
    }
});

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

// Both narrowed by CameraAccess — this is the data that drives the timeline strip and segment
// fetches for one specific camera, i.e. the same "can this principal actually review this camera's
// footage" question /playback-segment itself has to answer. Previously relied on the group's global
// Playback.View gate alone, same gap as /playback-segment's own (see that endpoint's own comment).
playbackApi.MapGet("/timeline", async (HttpContext ctx, Guid cameraId, DateTime from, DateTime to, int? buckets,
    ITimelineService timeline, ICameraAccessService cameraAccess, CancellationToken ct) =>
{
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Playback, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have playback access to this camera.", statusCode: StatusCodes.Status403Forbidden);
    return Results.Json(await timeline.GetBucketsAsync(cameraId, from, to, buckets ?? 200, ct));
});

playbackApi.MapGet("/segments", async (HttpContext ctx, Guid cameraId, DateTime from, DateTime to,
    ITimelineService timeline, ICameraAccessService cameraAccess, CancellationToken ct) =>
{
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Playback, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have playback access to this camera.", statusCode: StatusCodes.Status403Forbidden);
    return Results.Json(await timeline.GetSegmentsAsync(cameraId, from, to, ct));
});

// M18: the per-camera timeline's own bookmark markers — same Playback.View gate as the rest of this
// group, no per-camera CameraAccess narrowing (matches Bookmarks/Snapshots' own shared-visibility
// model, see BookmarkService's doc comment). Deliberately left as-is: unlike /timeline and /segments
// above (which hand back actual recording data for one camera), a bookmark is metadata that's
// already visible in full on the shared Bookmarks/Snapshots pages regardless of CameraAccess: this
// route narrowing while those don't would just be an inconsistent partial filter, not a real
// boundary. Revisit only if that shared-visibility model itself changes.
playbackApi.MapGet("/bookmarks", async (Guid cameraId, DateTime from, DateTime to, IBookmarkService bookmarks, CancellationToken ct) =>
    Results.Json(await bookmarks.ListForCameraAsync(cameraId, from, to, ct)));

// Merged across the given cameraIds (repeated query param) — the "was anything recording in this
// view" overview timeline on Pages/Playback, separate from the per-camera one above. cameraIds
// omitted falls back to every camera (see GetGlobalBucketsAsync's own doc comment) — playback-
// player.js always passes the current view's own camera set, so in practice this stays scoped to
// what's actually on screen rather than the whole system. Narrowed by CameraAccess regardless of
// what the caller passed: a restricted principal could otherwise still learn "something was
// recording/had motion somewhere" for a camera it can't open, either by omitting cameraIds (the
// documented every-camera fallback) or by naming a disallowed id directly — the merged buckets
// carry no camera identity themselves, but the boolean flags they carry are still a (weak) signal
// about cameras this principal has no access to.
app.MapGet("/api/timeline", async (HttpContext ctx, DateTime from, DateTime to, int? buckets, Guid[]? cameraIds,
    ITimelineService timeline, ICameraAccessService cameraAccess, CancellationToken ct) =>
{
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Playback, ct);
    var scopedCameraIds = accessible is null
        ? cameraIds
        : (cameraIds is null || cameraIds.Length == 0 ? accessible.ToArray() : cameraIds.Where(accessible.Contains).ToArray());
    return Results.Json(await timeline.GetGlobalBucketsAsync(from, to, buckets ?? 200, scopedCameraIds, ct));
}).RequireAuthorization("Playback.View");

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
// Filtered by CameraAccess, same action as /live itself — this returns every camera id with active
// motion system-wide with no per-camera scoping of its own, which let a CameraAccess-restricted
// principal enumerate camera GUIDs (and their live motion state) outside its grants entirely
// through this endpoint, then open them directly via /live.
app.MapGet("/api/cameras/motion-state", async (HttpContext ctx, ITimelineService timeline,
    ICameraAccessService cameraAccess, CancellationToken ct) =>
{
    var ids = await timeline.GetCamerasWithActiveMotionAsync(ct);
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.View, ct);
    if (accessible is not null) ids = ids.Where(accessible.Contains).ToList();
    return Results.Json(ids);
}).RequireAuthorization("Cameras.View");

// Companion to motion-state: which cameras are seeing a *classified* object right now
// (person/vehicle/face) rather than just movement. Separate endpoint rather than a wider
// motion-state payload so the existing badge keeps working untouched on any client that hasn't been
// updated, and so a deployment with no object-capable cameras pays nothing for it. Filtered by
// CameraAccess for the same reason motion-state above now is.
app.MapGet("/api/cameras/detection-state", async (HttpContext ctx, ITimelineService timeline,
    ICameraAccessService cameraAccess, CancellationToken ct) =>
{
    var states = await timeline.GetActiveDetectionsAsync(ct);
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.View, ct);
    if (accessible is not null) states = states.Where(s => accessible.Contains(s.CameraId)).ToList();
    return Results.Json(states);
}).RequireAuthorization("Cameras.View");

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
// policy), matching IndexModel's own gate — see that page's doc comment for why Dashboard.View isn't
// wired up here yet despite existing in PermissionCatalog since pass 3.
app.MapGet("/api/dashboard", async (HttpContext ctx, IDashboardService dashboardService, CancellationToken ct) =>
    Results.Json(await dashboardService.GetHealthAsync(ctx.User, ct))
).RequireAuthorization();

// ── REST API (M20 pass 1) ────────────────────────────────────────────────────
// Authenticated by ApiKeyAuthMiddleware above via the "X-Api-Key" header (or, since that middleware
// only ever *adds* a principal and never removes one, an admin's own cookie session also reaches this
// the same as any other [Authorize]'d page). Gated on Dashboard.View — already in PermissionCatalog,
// already seeded onto the built-in "API/Integration" role, unused anywhere else in the app until now.
// Cameras are scoped through CameraAccess exactly like Pages/Index and GET /api/dashboard already are
// (GetHealthAsync is reused unmodified) — a key bound to a camera-restricted role sees only what that
// role can see. Node storage has no such per-node ACL anywhere in this app, so it's a flat list of
// every node (GetAllNodeStatusAsync), not narrowed the way GetHealthAsync's own node tally is.
app.MapGet("/api/v1/status", async (HttpContext ctx, IDashboardService dashboardService, CancellationToken ct) =>
{
    var health = await dashboardService.GetHealthAsync(ctx.User, ct);
    var nodes = await dashboardService.GetAllNodeStatusAsync(ct);
    return Results.Json(new { cameras = health.Rows, nodes });
}).RequireAuthorization("Dashboard.View");

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
    HttpContext ctx, Guid cameraId, long segmentId, ITimelineService timeline, ICameraAccessService cameraAccess,
    IHttpClientFactory httpFactory, CancellationToken ct) =>
{
    // The group's Playback.View gate says this principal can review *some* camera's footage;
    // CameraAccess narrows it to *this* camera — this is the endpoint that actually streams the
    // video bytes, previously reachable for any cameraId once the global gate was met. Same fix as
    // /live's own (see that endpoint's own comment).
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Playback, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have playback access to this camera.", statusCode: StatusCodes.Status403Forbidden);

    var segment = await timeline.GetSegmentForPlaybackAsync(cameraId, segmentId, ct);
    if (segment is null) return Results.NotFound();
    if (segment.NodeIp is null || segment.NodeLivePort is null || segment.NodeMediaSigningKey is null)
    {
        return Results.Problem(
            "This segment's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var token = MediaToken.IssueForSegment(cameraId, segment.FilePath, segment.NodeMediaSigningKey, TimeSpan.FromSeconds(30));
    // M18 follow-up: forwarded as-is, not validated against anything here — same "not part of the
    // token, it only picks where inside an already-authorized file to start" reasoning as /live's own
    // ?role=. The node ignores it entirely (falls back to the whole file) below its own threshold or
    // once no usable fragment index exists, so there's nothing for this tier to double-check.
    var seekSecondsQuery = ctx.Request.Query["seekSeconds"].ToString();
    var seekSecondsPart = string.IsNullOrEmpty(seekSecondsQuery) ? "" : $"&seekSeconds={Uri.EscapeDataString(seekSecondsQuery)}";
    var nodeUri = $"http://{segment.NodeIp}:{segment.NodeLivePort}/playback-segment/{cameraId}" +
        $"?path={Uri.EscapeDataString(segment.FilePath)}&token={Uri.EscapeDataString(token)}{seekSecondsPart}";

    // Shorter than HttpClient's 100s default — a genuinely stuck node/storage read (confirmed
    // possible: a slow SMB share) shouldn't be able to hold this request open for nearly two
    // minutes with the browser just showing "Loading…" the whole time.
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(25);
    HttpResponseMessage nodeResponse;
    try
    {
        // See MediaTokenRequest's own doc comment: also sent as ?token= above for a node that
        // hasn't updated yet.
        nodeResponse = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Get, nodeUri, token),
            HttpCompletionOption.ResponseHeadersRead, ct);
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

    // Present only when the node actually served a partial (fragment-aligned) response — absent for
    // the ordinary whole-file case, which the client already treats as "started at true segment
    // start" without needing to be told so explicitly.
    if (nodeResponse.Headers.TryGetValues("X-Fragment-Start-Seconds", out var fragmentStartValues))
    {
        ctx.Response.Headers["X-Fragment-Start-Seconds"] = fragmentStartValues.FirstOrDefault();
    }

    return Results.Stream(await nodeResponse.Content.ReadAsStreamAsync(ct), "video/mp4");
}).RequireAuthorization("Playback.View");

// M7 pass 2: same "browser never talks to a node directly" proxy shape as /playback-segment above,
// but for one extracted JPEG hover-preview frame instead of a whole segment's bytes. Shared by both
// the bucketed/historical lookup (atUtc-based) below and the Dashboard's "most recent" lookup —
// everything past resolving a ThumbnailInfo is identical proxy plumbing (token, node URI, relay).
// 150 mirrors LarisVMS.Media.ThumbnailCapture.DefaultMaxDimension — duplicated as a literal rather
// than referenced, since LarisVMS.Web has no project reference to LarisVMS.Media (it never runs
// ffmpeg itself, only proxies to a node that does) and taking one on just for this constant isn't
// worth the coupling.
async Task<IResult> ProxyThumbnailAsync(Guid cameraId, ThumbnailInfo? thumb, IHttpClientFactory httpFactory, CancellationToken ct, HttpContext httpContext, bool longLivedCache, int maxDimension = 150, int quality = 8)
{
    if (thumb is null) return Results.NotFound();
    if (thumb.NodeIp is null || thumb.NodeLivePort is null || thumb.NodeMediaSigningKey is null)
    {
        return Results.Problem(
            "This segment's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // Shorter than /playback-segment's 25s — this relays one small JPEG frame, not a video segment.
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(15);

    Task<HttpResponseMessage> RequestAsync(int offsetSeconds)
    {
        var token = MediaToken.IssueForThumbnail(cameraId, thumb.FilePath, offsetSeconds, thumb.NodeMediaSigningKey!, TimeSpan.FromSeconds(30));
        var nodeUri = $"http://{thumb.NodeIp}:{thumb.NodeLivePort}/playback-thumbnail/{cameraId}" +
            $"?path={Uri.EscapeDataString(thumb.FilePath)}&offset={offsetSeconds}&token={Uri.EscapeDataString(token)}&maxDim={maxDimension}&q={quality}";
        // See MediaTokenRequest's own doc comment: also sent as ?token= above for a node that
        // hasn't updated yet.
        return client.SendAsync(MediaTokenRequest.Create(HttpMethod.Get, nodeUri, token), HttpCompletionOption.ResponseHeadersRead, ct);
    }

    HttpResponseMessage nodeResponse;
    try
    {
        nodeResponse = await RequestAsync(thumb.OffsetSeconds);

        // Segment.DurationMs is derived from wall-clock EndUtc-StartUtc (NodeService.RecordSegmentsAsync),
        // not a re-measurement of the file's actual encoded duration — the two can drift by a second or
        // so, and GetExactThumbnailInfoAsync's offset (unlike the bucketed lookup's near-universal 0,
        // always safe since any valid segment file has content at its own start) routinely targets
        // right up against that clamp, close to the segment's believed end. ffmpeg's -ss there is
        // unclamped against the *real* file (ThumbnailCapture.cs), so a small mismatch makes it seek
        // past actual content and the node correctly reports that as 502 rather than hanging — confirmed
        // live as soon as the exact-offset lookup shipped. Retrying once at offset 0 on the very same
        // segment/file trades exact-instant precision for a guaranteed hit, only on this already-failed
        // path — cheaper and more robust than trying to predict how much clamp margin is "enough".
        if (nodeResponse.StatusCode == (System.Net.HttpStatusCode)StatusCodes.Status502BadGateway && thumb.OffsetSeconds != 0)
        {
            nodeResponse.Dispose();
            nodeResponse = await RequestAsync(0);
        }
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

    // longLivedCache is false only for the "latest" lookup (Dashboard) — that one's whole point is
    // showing whatever's newest, so caching it would just freeze the dashboard on the first frame it
    // ever saw. Every atUtc-based lookup (hover-preview and Snapshots' exact cards alike) targets a
    // specific already-recorded instant, so the browser can skip the network on most repeat views
    // rather than re-fetching bytes it already has — confirmed live as the real cost behind Snapshots
    // feeling uncached even though the node's own on-disk cache was already being hit: every reload
    // still paid a full round trip per card.
    //
    // Deliberately NOT `immutable`/a year-long max-age, even though the underlying footage itself
    // never changes: the *extraction* can — this exact deployment already has one real example, a
    // batch of pre-M18 cache files on disk with a different naming scheme than this code now expects
    // (no `_{maxDim}q{quality}` suffix), proof the node-side generation logic has changed under
    // already-served URLs before and could again (a future quality/crop fix, say). A day-scale
    // max-age cuts the network round trip for the case this exists to fix (reloading the same page
    // again shortly after) while any such change still self-heals within a bounded window, with no
    // manual cache-busting scheme needed. `private`, not `public` — this is an authenticated,
    // per-user-permissioned resource (RequireAuthorization below), not something a shared/intermediary
    // cache should ever store.
    if (longLivedCache) httpContext.Response.Headers.CacheControl = "private, max-age=86400";

    return Results.Stream(await nodeResponse.Content.ReadAsStreamAsync(ct), "image/jpeg");
}

// GetThumbnailInfoAsync resolves which segment covers atUtc and buckets/clamps the offset within it
// server-side. exact=true (M18: Pages/Snapshots) skips the 5-minute bucketing and resolves the exact
// requested instant instead — see GetExactThumbnailInfoAsync's own doc comment for why the hover-scrub
// caller above can't just always do this. `exact = false` (a real C# default, not just bool's usual
// zero-value) is required, not cosmetic: a minimal-API primitive parameter with no default is treated
// as a *required* query-string value, and every caller but Snapshots omits `exact` entirely — without
// this default the framework rejected every one of those (the hover-preview path that predates this
// parameter) outright with 400 Bad Request. Confirmed live: every hover-thumbnail request broke the
// moment this parameter was added without one.
app.MapGet("/playback-thumbnail/{cameraId:guid}", async (
    Guid cameraId, DateTime atUtc, ITimelineService timeline, ICameraAccessService cameraAccess,
    IHttpClientFactory httpFactory, CancellationToken ct, HttpContext httpContext, bool exact = false) =>
{
    // Narrowed by CameraAccess same as /playback-segment and the playbackApi group — this is the
    // Snapshots-card and hover-scrub lookup, and previously relied on the group's global
    // Playback.View gate alone with no per-camera check of its own.
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(httpContext.User, CameraAccessActions.Playback, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have playback access to this camera.", statusCode: StatusCodes.Status403Forbidden);

    return await ProxyThumbnailAsync(cameraId,
        exact ? await timeline.GetExactThumbnailInfoAsync(cameraId, atUtc, ct) : await timeline.GetThumbnailInfoAsync(cameraId, atUtc, ct),
        httpFactory, ct, httpContext, longLivedCache: true,
        // exact is Snapshots' own card thumbnails — something a viewer actually looks closely at, not
        // a fleeting scrub-hover preview, so it gets a genuinely detailed frame. The cap is on the
        // *longer* edge (scale=N:N:force_original_aspect_ratio=decrease), so on a 16:9 source 1280
        // yields 1280x720 — the requested 720 vertical. An earlier 854 was the same arithmetic aimed
        // at 480 vertical, which was reported as still too low to make out what's in a frame. Every
        // common ratio clears the 480-vertical floor at this cap (4:3 -> 960, 1:1 -> 1280, portrait
        // -> 1280); only genuinely ultrawide sources (21:9 -> 549, 2.39:1 -> 536) land between 480
        // and 720, which is inherent to the shape rather than something a bigger cap would fix.
        //
        // -q:v also eases from 8 to 4: at 150px nobody reads detail out of a hover preview so heavy
        // compression is free there, but at 1280px JPEG artifacts were themselves part of "hard to
        // make out" — resolution alone wouldn't have fixed it.
        maxDimension: exact ? 1280 : 150,
        quality: exact ? 4 : 8);
}).RequireAuthorization("Playback.View");

// Dashboard's "most recent thumbnail" column (Pages/Index, dashboard.js) — plain [Authorize], not
// Playback.View, matching Pages/Index's own gate (IndexModel has no specific resource policy) rather
// than the stricter one the historical/scrub lookup above uses. Still narrowed by CameraAccess
// (View, matching the Dashboard's own per-row filtering in DashboardService) even though the group
// gate stays plain-authenticated: this is per-camera hiding, not a new RBAC requirement, so a
// restricted camera stays hidden even if this endpoint is hit directly rather than only through a
// Dashboard row that was already filtered out.
app.MapGet("/playback-thumbnail/{cameraId:guid}/latest", async (
    Guid cameraId, ITimelineService timeline, ICameraAccessService cameraAccess,
    IHttpClientFactory httpFactory, CancellationToken ct, HttpContext httpContext) =>
{
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(httpContext.User, CameraAccessActions.View, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have access to this camera.", statusCode: StatusCodes.Status403Forbidden);

    return await ProxyThumbnailAsync(cameraId, await timeline.GetLatestThumbnailInfoAsync(cameraId, ct), httpFactory, ct, httpContext, longLivedCache: false);
}).RequireAuthorization();

// Object detection plan decision 10: same "browser never talks to a node directly" proxy shape as
// ProxyThumbnailAsync above, but for one AI-detection span's cropped best-frame image instead of a
// plain frame at a given instant — see GetSnapshotImageInfoAsync's own doc comment for how the
// underlying segment/offset/box get resolved.
async Task<IResult> ProxySnapshotImageAsync(Guid cameraId, SnapshotImageInfo? info, IHttpClientFactory httpFactory, CancellationToken ct, HttpContext httpContext)
{
    if (info is null) return Results.NotFound();
    if (info.NodeIp is null || info.NodeLivePort is null || info.NodeMediaSigningKey is null)
    {
        return Results.Problem(
            "This segment's node hasn't reported live-view readiness yet (needs at least one heartbeat since being upgraded to a build with live view).",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(15);

    // Reuses the existing hover-thumbnail token (cameraId+filePath+offsetSeconds) — see the node-side
    // route's own doc comment for why that's the right security boundary here too. The box/frame
    // dimensions ride unsigned, same "quality knob, not tamper-sensitive" reasoning maxDim/q already
    // use on the thumbnail proxy.
    Task<HttpResponseMessage> RequestAsync(int offsetSeconds)
    {
        var token = MediaToken.IssueForThumbnail(cameraId, info.FilePath, offsetSeconds, info.NodeMediaSigningKey!, TimeSpan.FromSeconds(30));
        var nodeUri = $"http://{info.NodeIp}:{info.NodeLivePort}/snapshot-image/{cameraId}" +
            $"?path={Uri.EscapeDataString(info.FilePath)}&offset={offsetSeconds}&token={Uri.EscapeDataString(token)}" +
            $"&spanId={info.SpanId}" +
            $"&x={info.BoxX.ToString(CultureInfo.InvariantCulture)}&y={info.BoxY.ToString(CultureInfo.InvariantCulture)}" +
            $"&w={info.BoxW.ToString(CultureInfo.InvariantCulture)}&h={info.BoxH.ToString(CultureInfo.InvariantCulture)}" +
            $"&frameW={info.FrameWidth}&frameH={info.FrameHeight}";
        // See MediaTokenRequest's own doc comment: also sent as ?token= above for a node that
        // hasn't updated yet.
        return client.SendAsync(MediaTokenRequest.Create(HttpMethod.Get, nodeUri, token), HttpCompletionOption.ResponseHeadersRead, ct);
    }

    HttpResponseMessage nodeResponse;
    try
    {
        nodeResponse = await RequestAsync(info.OffsetSeconds);

        // Same drift ProxyThumbnailAsync's own identical retry already documents: Segment.DurationMs
        // is wall-clock derived (NodeService.RecordSegmentsAsync), not a re-measurement of the file's
        // actual encoded duration, and GetSnapshotImageInfoAsync's offset is clamped against that
        // same possibly-inflated value — so it can still land past the real content, especially for a
        // best-frame instant near a short/truncated segment's believed end. Confirmed live: two
        // AI-detection spans 502'd on every load, offset unclamped against the real file each time,
        // while every other span on the same page succeeded. Retrying once at offset 0 on the same
        // segment/box trades exact-instant precision for a guaranteed hit, only on this already-failed
        // path — the box coordinates are unaffected, so the crop position is still correct, just from
        // an earlier frame in the same recording.
        if (nodeResponse.StatusCode == (System.Net.HttpStatusCode)StatusCodes.Status502BadGateway && info.OffsetSeconds != 0)
        {
            nodeResponse.Dispose();
            nodeResponse = await RequestAsync(0);
        }
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

    // Same day-scale, private cache reasoning as ProxyThumbnailAsync's own exact-instant lookups —
    // keyed uniquely by spanId, so a repeat view of the same card skips the network round trip.
    httpContext.Response.Headers.CacheControl = "private, max-age=86400";

    return Results.Stream(await nodeResponse.Content.ReadAsStreamAsync(ct), "image/jpeg");
}

app.MapGet("/snapshot-image/{cameraId:guid}/{spanId:long}", async (
    Guid cameraId, long spanId, ITimelineService timeline, ICameraAccessService cameraAccess,
    IHttpClientFactory httpFactory, CancellationToken ct, HttpContext httpContext) =>
{
    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(httpContext.User, CameraAccessActions.Playback, ct);
    if (accessible is not null && !accessible.Contains(cameraId))
        return Results.Problem("You don't have playback access to this camera.", statusCode: StatusCodes.Status403Forbidden);

    return await ProxySnapshotImageAsync(cameraId, await timeline.GetSnapshotImageInfoAsync(cameraId, spanId, ct), httpFactory, ct, httpContext);
}).RequireAuthorization("Playback.View");

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
        // See MediaTokenRequest's own doc comment: also sent as ?token= above for a node that
        // hasn't updated yet.
        nodeResponse = await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Get, nodeUri, token),
            HttpCompletionOption.ResponseHeadersRead, ct);
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
            // See MediaTokenRequest's own doc comment: also sent as ?token= above for a node that
            // hasn't updated yet.
            await client.SendAsync(MediaTokenRequest.Create(HttpMethod.Delete, nodeUri, token), ct);
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

// ── Bookmarks (M18) ──────────────────────────────────────────────────────────
// Triggered from Playback's own toolbar. Roles/permissions overhaul, pass 3: creating a bookmark
// now also requires Bookmarks.Edit (its own coarse gate, distinct from Playback.View — a role can
// hold one without the other, e.g. API/Integration keeps limited Playback but never Bookmarks),
// still narrowed per-camera by CameraAccessActions.Playback — a viewer who can't play this camera
// back has nothing here worth marking either. Listing/deleting a bookmark is a server-rendered Razor
// Page (Pages/Bookmarks/Index), not a JSON API — only *creating* one happens from the JS-driven
// Playback page, so only that direction needs a route here.
app.MapPost("/api/bookmarks", async (HttpContext ctx, CreateBookmarkRequest request,
    IBookmarkService bookmarkService, ICameraAccessService cameraAccess, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Note))
        return Results.BadRequest("Enter a note for this bookmark.");

    var accessible = await cameraAccess.GetAccessibleCameraIdsAsync(ctx.User, CameraAccessActions.Playback, ct);
    if (accessible is not null && !accessible.Contains(request.CameraId))
        return Results.Problem("You don't have playback access to this camera.", statusCode: StatusCodes.Status403Forbidden);

    var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    var bookmark = await bookmarkService.CreateAsync(request.CameraId, request.TimestampUtc, request.Note.Trim(),
        userId, ctx.User.Identity?.Name, ct);
    return Results.Json(new { id = bookmark.Id });
}).RequireAuthorization("Playback.View", "Bookmarks.Edit");

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

// Object detection plan decision 6: unlike ProxyLiveViewAsync's pure byte relay, each tick here is
// parsed, enriched with a color per box (Node/Vision Service have no database access — this proxy
// is the only tier that can attach one), and re-serialized before being forwarded to the browser.
// A single malformed or unparseable tick is skipped rather than tearing down the whole viewer
// connection over it — the next poll (at most ~150ms later, per DetectionOverlayHandler's own
// cadence) supersedes it anyway.
static async Task ProxyDetectionOverlayAsync(WebSocket node, WebSocket browser, DetectedObjectCategoryColorCache colorCache, CancellationToken ct)
{
    var buffer = new byte[64 * 1024];
    // The re-serialize below has to land as the camelCase this app's JS everywhere else expects
    // (Results.Json — the framework's own default for Minimal APIs — and this app's own
    // hand-written anonymous-object endpoints both already use it). Confirmed live as the reason no
    // box ever drew despite the socket streaming real data every tick: plain SerializeToUtf8Bytes
    // with no options defaults to PascalCase, live-view.js's draw() reads box.movementState/box.x/
    // etc., and a property read against the wrong casing is silently undefined in JS — canvas draws
    // NaN coordinates as a no-op, no exception anywhere in the chain. Built once per connection, not
    // per tick — this loop runs at ~6.7Hz for as long as a viewer has the overlay open.
    var camelCaseJson = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
    try
    {
        while (node.State == WebSocketState.Open && browser.State == WebSocketState.Open)
        {
            using var messageBuffer = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await node.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                messageBuffer.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            List<VisionLiveDetectionBox> boxes;
            try
            {
                boxes = System.Text.Json.JsonSerializer.Deserialize<List<VisionLiveDetectionBox>>(messageBuffer.ToArray()) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            var enriched = new List<object>(boxes.Count);
            foreach (var box in boxes)
            {
                var colorHex = await colorCache.GetColorAsync(box.Category, ct);
                enriched.Add(new
                {
                    box.TrackId, box.Category, box.Label, box.MovementState,
                    box.X, box.Y, box.W, box.H, box.Confidence, ColorHex = colorHex
                });
            }

            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(enriched, camelCaseJson);
            await browser.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
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
