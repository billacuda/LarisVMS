using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rcordr.Core.Dtos;
using Rcordr.Core.Entities;
using Rcordr.Core.Interfaces;
using Rcordr.Infrastructure.Auth;
using Rcordr.Infrastructure.Data;
using Rcordr.Infrastructure.Repositories;
using Rcordr.Infrastructure.Security;
using Rcordr.Infrastructure.Services;
using Rcordr.Web.Health;
using Rcordr.Web.Middleware;

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
var dataProtectionPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rcordr", "keys");
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

// ── ONVIF HTTP client ────────────────────────────────────────────────────────
// CameraService takes a Func<HttpClient> rather than IHttpClientFactory directly so
// Rcordr.Infrastructure doesn't need a package reference just for the factory interface — Web
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
    // LastSeenAt/Version/Status are already updated by NodeAuthMiddleware's AuthenticateAsync call
    // for every authenticated request — the heartbeat endpoint only needs to record disk usage (the
    // node is the only side that can measure its own storage root) and hand back the interval.
    var node = (Node)ctx.Items[NodeAuthMiddleware.HttpContextItemKey]!;
    await nodeService.UpdateStorageStatsAsync(node.Id, request.FreeBytes, request.TotalBytes, ct);
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

app.Run();

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
