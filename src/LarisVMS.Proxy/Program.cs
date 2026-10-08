using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Dtos;
using LarisVMS.Proxy;
using LarisVMS.Relay;

// ── First-run registration ──────────────────────────────────────────────────
// If proxy.config doesn't exist yet, this run must be given --server-url and --registration-key
// (what install-proxy.ps1 passes) to register once; the assigned ProxyId/secret are then persisted.
var config = ProxyConfigStore.Load();
var insecureTls = HasFlag(args, "--insecure-tls") || Environment.GetEnvironmentVariable("LARISVMS_INSECURE_TLS") == "1";

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

    var registerClient = new ProxyApiClient(serverUrl, insecureTls);
    var response = await registerClient.RegisterAsync(
        new ProxyRegisterRequest(registrationKey, Environment.MachineName, ProxyVersion.Current, "win-x64"),
        CancellationToken.None);
    config = new ProxyConfig(serverUrl, response.ProxyId, response.Secret);
    ProxyConfigStore.Save(config);
    Console.WriteLine($"Registered as proxy {response.ProxyId}.");
}

var apiClient = new ProxyApiClient(config.ServerUrl, insecureTls);
apiClient.SetCredentials(config.ProxyId, config.Secret);

var builder = WebApplication.CreateBuilder(args);

var proxyFileLogger = new LarisVMS.Core.Logging.FileLoggerProvider(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "logs"),
    "proxy", LogLevel.Information);
builder.Logging.AddProvider(proxyFileLogger);
var frameworkFilter = LarisVMS.Core.Logging.FrameworkLogFilter.HiddenUnlessDebug(proxyFileLogger);
builder.Logging.AddFilter("Microsoft.AspNetCore", frameworkFilter);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", frameworkFilter);

// ── HTTPS listener ──────────────────────────────────────────────────────────
var endpoint = ProxyEndpointConfig.Resolve(config.CachedConfig);
var selfSignedPfx = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS", "proxy-endpoint.selfsigned.pfx");
var certHolder = new CertHolder(
    new CertHolderOptions(endpoint.PfxPath, endpoint.PfxPassword, endpoint.AllowInsecure, endpoint.Host, selfSignedPfx),
    proxyFileLogger.CreateLogger("Endpoint"));
if (certHolder.Load())
    Console.WriteLine($"Proxy HTTPS endpoint: binding port {endpoint.Port} " +
        $"({(certHolder.IsSelfSigned ? "self-signed" : "supplied certificate")}).");
else
    Console.Error.WriteLine($"Proxy HTTPS endpoint has no usable certificate: {certHolder.LastError}");

builder.WebHost.ConfigureKestrel(o =>
{
    if (certHolder.Current is not null)
        o.ListenAnyIP(endpoint.Port, lo => lo.UseHttps(h => h.ServerCertificateSelector = (_, _) => certHolder.Current));
    else
        // No cert — still bind so /health answers (unhealthy) and the operator sees the error.
        o.ListenAnyIP(endpoint.Port);
});

builder.Services.AddWindowsService(o => o.ServiceName = "LarisVMS Proxy");
builder.Services.AddSingleton(apiClient);
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(endpoint);
builder.Services.AddSingleton(certHolder);
builder.Services.AddSingleton(sp => new ProxyUpdateService(
    config, insecureTls, sp.GetRequiredService<ILoggerFactory>().CreateLogger<ProxyUpdateService>(),
    sp.GetRequiredService<IHostApplicationLifetime>()));
builder.Services.AddSingleton<ProxyWorker>();
builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ProxyWorker>());
builder.Services.AddSingleton<IHostedService>(sp => new CertWatcherService(
    certHolder, sp.GetRequiredService<ILoggerFactory>().CreateLogger<CertWatcherService>()));

var app = builder.Build();
// Tidy up after the last auto-update, and say so in this log if it never got applied.
app.Services.GetRequiredService<ProxyUpdateService>().CheckLastUpdate();
app.UseWebSockets();

var relayLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Relay");

// Health — polled by LarisVMS.Web's ProxyHealthMonitor every ~15s.
app.MapGet("/health", () => Results.Json(new { version = ProxyVersion.Current, status = "ok" }));

app.MapGet("/", () => Results.Content(
    "LarisVMS media proxy. If you opened this page to trust its certificate, that's done — you can close this tab.",
    "text/plain"));

// Live view — a dumb WebSocket pass-through: browser <-> proxy <-> node. The central-minted ?token=
// is forwarded verbatim (it is bound to the node's own signing key; the proxy never validates it).
app.MapGet("/live/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, ProxyWorker worker) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    if (worker.Resolve(cameraId) is not { } target)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsync("This proxy is not configured to serve that camera.");
        return;
    }

    var token = ctx.Request.Query["token"].ToString();
    var role = ctx.Request.Query["role"].ToString();
    var nodeUri = new Uri($"ws://{target.Host}:{target.Port}/live/{cameraId}" +
        $"?token={Uri.EscapeDataString(token)}" +
        (string.Equals(role, "sub", StringComparison.OrdinalIgnoreCase) ? "&role=sub" : ""));

    using var nodeSocket = new ClientWebSocket();
    if (!string.IsNullOrEmpty(token))
        nodeSocket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
    try
    {
        await nodeSocket.ConnectAsync(nodeUri, ctx.RequestAborted);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync($"Could not reach recorder node: {ex.Message}");
        return;
    }

    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
    relayLogger.LogInformation("Live relay started: camera {CameraId} -> node {NodeHost} for client {ClientIp}.",
        cameraId, target.Host, clientIp);
    using var browserSocket = await ctx.WebSockets.AcceptWebSocketAsync();
    try
    {
        await WebSocketRelay.RelayAsync(browserSocket, nodeSocket, ctx.RequestAborted);
    }
    finally
    {
        relayLogger.LogInformation("Live relay ended: camera {CameraId} -> client {ClientIp}.", cameraId, clientIp);
    }
});

// Playback segment — a dumb HTTP stream pass-through. LarisVMS.Web 302-redirects the browser here;
// the request arrives cross-origin, so CORS headers (and exposing X-Fragment-Start-Seconds) match
// what the node itself sends for a direct connection.
var playbackHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
app.MapGet("/playback-segment/{cameraId:guid}", async (HttpContext ctx, Guid cameraId, ProxyWorker worker) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    var allowOrigin = !string.IsNullOrEmpty(worker.WebOrigin) ? worker.WebOrigin
        : !string.IsNullOrEmpty(origin) ? origin : "*";
    ctx.Response.Headers.AccessControlAllowOrigin = allowOrigin;
    ctx.Response.Headers["Access-Control-Expose-Headers"] = "X-Fragment-Start-Seconds";
    ctx.Response.Headers["Vary"] = "Origin";

    if (worker.Resolve(cameraId) is not { } target)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var token = ctx.Request.Query["token"].ToString();
    var path = ctx.Request.Query["path"].ToString();
    var seekSeconds = ctx.Request.Query["seekSeconds"].ToString();
    if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(path))
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var nodeUri = $"http://{target.Host}:{target.Port}/playback-segment/{cameraId}" +
        $"?path={Uri.EscapeDataString(path)}&token={Uri.EscapeDataString(token)}" +
        (string.IsNullOrEmpty(seekSeconds) ? "" : $"&seekSeconds={Uri.EscapeDataString(seekSeconds)}");

    using var request = new HttpRequestMessage(HttpMethod.Get, nodeUri);
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    HttpResponseMessage nodeResponse;
    try
    {
        nodeResponse = await playbackHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
        await ctx.Response.WriteAsync($"Could not reach recorder node: {ex.Message}");
        return;
    }

    using (nodeResponse)
    {
        ctx.Response.StatusCode = (int)nodeResponse.StatusCode;
        if (nodeResponse.Headers.TryGetValues("X-Fragment-Start-Seconds", out var frag))
            ctx.Response.Headers["X-Fragment-Start-Seconds"] = frag.FirstOrDefault();
        if (nodeResponse.IsSuccessStatusCode)
        {
            ctx.Response.ContentType = nodeResponse.Content.Headers.ContentType?.ToString() ?? "video/mp4";
            await nodeResponse.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
    }
});

await app.RunAsync();

static string? GetArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
