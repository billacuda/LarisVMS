using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rcordr.Media;
using Rcordr.Node;

// ── First-run registration ──────────────────────────────────────────────────
// If node.config doesn't exist yet, this run must be given --server-url and --registration-key
// (what install-node.ps1 passes) to register once; the assigned NodeId/secret are then persisted
// and every subsequent run just loads them.
var config = NodeConfigStore.Load();
if (config is null)
{
    var serverUrl = GetArg(args, "--server-url") ?? Environment.GetEnvironmentVariable("RCORDR_SERVER_URL");
    var registrationKey = GetArg(args, "--registration-key") ?? Environment.GetEnvironmentVariable("RCORDR_REGISTRATION_KEY");
    if (serverUrl is null || registrationKey is null)
    {
        Console.Error.WriteLine("Not yet registered. Run with --server-url <url> --registration-key <key> " +
            "(or set RCORDR_SERVER_URL / RCORDR_REGISTRATION_KEY) the first time.");
        Environment.Exit(1);
        return;
    }

    var insecure = HasFlag(args, "--insecure-tls") || Environment.GetEnvironmentVariable("RCORDR_INSECURE_TLS") == "1";
    var registerClient = new NodeApiClient(serverUrl, insecure);
    var response = await registerClient.RegisterAsync(
        new Rcordr.Core.Dtos.NodeRegisterRequest(registrationKey, Environment.MachineName, "0.4.0", Environment.OSVersion.Platform.ToString()),
        CancellationToken.None);

    config = new NodeConfig(serverUrl, response.NodeId, response.Secret);
    NodeConfigStore.Save(config);
    Console.WriteLine($"Registered as node {response.NodeId}.");
}

var insecureTls = HasFlag(args, "--insecure-tls") || Environment.GetEnvironmentVariable("RCORDR_INSECURE_TLS") == "1";
var apiClient = new NodeApiClient(config.ServerUrl, insecureTls);
apiClient.SetCredentials(config.NodeId, config.Secret);

var ffmpegPath = FfmpegPathResolver.Resolve(
    GetArg(args, "--ffmpeg-path") ?? Environment.GetEnvironmentVariable("RCORDR_FFMPEG_PATH"));
if (!await FfmpegPathResolver.IsRunnableAsync(ffmpegPath))
{
    Console.Error.WriteLine($"ffmpeg could not be run at '{ffmpegPath}'. Set --ffmpeg-path or RCORDR_FFMPEG_PATH, " +
        "or install ffmpeg and ensure it's on PATH.");
    Environment.Exit(1);
    return;
}

var fallbackStorageRoot = GetArg(args, "--storage-root") ?? Environment.GetEnvironmentVariable("RCORDR_STORAGE_ROOT")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Rcordr", "recordings");

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "Rcordr Node");
builder.Services.AddSingleton(apiClient);
builder.Services.AddSingleton<IHostedService>(sp => new NodeWorker(
    apiClient, ffmpegPath, fallbackStorageRoot, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IHostedService>(sp => new StorageManager(
    apiClient, fallbackStorageRoot, sp.GetRequiredService<ILoggerFactory>().CreateLogger<StorageManager>()));

await builder.Build().RunAsync();

static string? GetArg(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
