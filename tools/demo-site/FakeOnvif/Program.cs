// Fake ONVIF cameras for the LarisVMS demo site. See ..\README.md.
//
//   dotnet run --project tools\demo-site\FakeOnvif -- --config tools\demo-site\cameras.json
//
// One HTTP listener per camera IP (port 80) answers the ONVIF device, media and events calls
// LarisVMS makes; video comes from MediaMTX on the same IPs (port 554). Credentials are accepted
// but not checked.

using System.Net;
using System.Xml;
using System.Xml.Linq;
using FakeOnvif;

var builder = WebApplication.CreateBuilder(args);
var configPath = builder.Configuration["config"]
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "cameras.json"));
var demo = DemoConfig.Load(configPath);

builder.Services.AddSingleton(demo);
builder.Services.AddSingleton<EventHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EventHub>());
builder.Services.AddSingleton<SnapshotCache>();
builder.Services.AddHostedService<WsDiscoveryResponder>();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    foreach (var camera in demo.Cameras) kestrel.Listen(IPAddress.Parse(camera.Ip), 80);
});

var app = builder.Build();

CameraConfig? CameraFor(HttpContext ctx) =>
    ctx.Connection.LocalIpAddress is { } ip ? demo.FindByIp(ip.MapToIPv4().ToString()) : null;

app.MapPost("/onvif/{**path}", async (HttpContext ctx, string? path, EventHub events, ILogger<Program> log) =>
{
    var camera = CameraFor(ctx);
    if (camera is null) return Results.NotFound();

    XElement? operation;
    try
    {
        var doc = await XDocument.LoadAsync(ctx.Request.Body, LoadOptions.None, ctx.RequestAborted);
        operation = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault();
    }
    catch (XmlException) { operation = null; }
    if (operation is null) return Xml(Soap.Fault("InvalidArgVal", "Malformed SOAP request."), 400);

    string? Arg(string localName) => operation.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();
    TimeSpan? Duration(string localName)
    {
        var text = Arg(localName);
        try { return text is null ? null : XmlConvert.ToTimeSpan(text); } catch (FormatException) { return null; }
    }

    // Subscription endpoints look like /onvif/event_service/sub/<id>.
    var subscriptionId = path?.StartsWith("event_service/sub/", StringComparison.OrdinalIgnoreCase) == true
        ? path["event_service/sub/".Length..] : null;

    var name = operation.Name.LocalName;
    log.LogDebug("{Camera} {Operation}", camera.Id, name);

    string? body = name switch
    {
        "GetDeviceInformation" => Soap.DeviceInformation(camera),
        "GetCapabilities" => Soap.Capabilities(camera),
        "GetServices" => Soap.Services(camera),
        "GetSystemDateAndTime" => Soap.SystemDateAndTime(),
        "GetScopes" => Soap.Scopes(camera),
        "GetProfiles" => Soap.Profiles(camera),
        "GetVideoSources" => Soap.VideoSources(),
        "GetStreamUri" => Soap.StreamUri(camera, demo.RtspPort, Arg("ProfileToken")),
        "GetSnapshotUri" => Soap.SnapshotUri(camera),
        "GetEventProperties" => EventHub.EventProperties(),
        "CreatePullPointSubscription" => events.CreatePullPoint(camera, Duration("InitialTerminationTime")),
        "PullMessages" when subscriptionId is not null => await events.PullAsync(subscriptionId,
            Duration("Timeout") ?? TimeSpan.FromSeconds(10),
            int.TryParse(Arg("MessageLimit"), out var limit) ? limit : 10, ctx.RequestAborted),
        "Renew" when subscriptionId is not null => events.Renew(subscriptionId),
        "Unsubscribe" when subscriptionId is not null => events.Unsubscribe(subscriptionId),
        _ => "",
    };

    if (body is null) return Xml(Soap.Fault("InvalidArgVal", "Unknown or expired subscription."), 400);
    if (body.Length == 0)
    {
        log.LogInformation("{Camera}: {Operation} isn't implemented; returned ActionNotSupported", camera.Id, name);
        return Xml(Soap.Fault("ActionNotSupported", $"{name} is not supported by this device."), 400);
    }
    return Xml(Soap.Envelope(body), 200);
});

app.MapGet("/snapshot.jpg", async (HttpContext ctx, SnapshotCache snapshots) =>
{
    var camera = CameraFor(ctx);
    if (camera is null) return Results.NotFound();
    var jpeg = await snapshots.GetAsync(camera, ctx.RequestAborted);
    return jpeg is null ? Results.StatusCode(503) : Results.File(jpeg, "image/jpeg");
});

app.MapGet("/", (HttpContext ctx) => CameraFor(ctx) is { } c
    ? Results.Text($"{CameraConfig.Manufacturer} {c.Model} \"{c.Name}\" (fake ONVIF camera for the LarisVMS demo)")
    : Results.NotFound());

app.Logger.LogInformation("Fake cameras: {Cameras}", string.Join(", ", demo.Cameras.Select(c => $"{c.Id}@{c.Ip}")));
app.Run();

static IResult Xml(string xml, int status) => Results.Content(xml, Soap.ContentType, System.Text.Encoding.UTF8, status);
