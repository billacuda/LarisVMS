using System.Globalization;
using System.Text.Json;
using LarisVMS.Core.Security;
using LarisVMS.Core.Setup;

namespace LarisVMS.Node;

/// <summary>
/// <c>sudo larisvms-node setup ...</c>: pairs a Linux node installed from the .deb or .rpm with a
/// server — the Linux counterpart of the MSI's settings and install-node.ps1. It writes
/// <c>/etc/larisvms/node.env</c> (read by larisvms-node.service), the one-time registration key,
/// and the optional client endpoint, prepares the recordings folders for the larisvms user, opens the
/// firewall, and restarts the service, which registers on its next start.
/// </summary>
internal static class NodeSetupCommand
{
    public const string Usage = """
        Usage: sudo larisvms-node setup --server-url <url> --registration-key <key> [options]

          --server-url <url>               The LarisVMS server, e.g. https://vms.example.com:8444
          --registration-key <key>         From the server's Add node page (first setup only)
          --server-cert-thumbprint <hex>   Pin the server certificate's SHA-256 thumbprint before registering
                                           (Nodes page > Updates and registration > Server certificate)
          --storage-root <path>            Recordings folder (default /var/lib/larisvms/recordings)
          --archive-root <path>            Optional archive folder
          --live-port <port>               Live view port the server and proxies reach (default 8554)
          --live-allow-from <a,b>          Only these IPs or CIDR blocks may reach the live port
          --client-port <port>             Optional HTTPS port browsers reach directly
          --client-pfx-path <path>         Certificate for it (default: self-signed)
          --client-pfx-password <secret>
          --client-allow-insecure          Allow plain HTTP there
          --client-endpoint-host <name>    The host name browsers use for it
          --ffmpeg-path <path>             ffmpeg, if it isn't on PATH
          --reregister                     Forget the current registration and register again

        Run again with any of these to change them; the service restarts with the new settings.
        """;

    private static readonly HashSet<string> Flags = ["--reregister", "--client-allow-insecure"];

    private static readonly HashSet<string> Options =
    [
        "--server-url", "--registration-key", "--server-cert-thumbprint", "--storage-root", "--archive-root",
        "--live-port", "--live-allow-from", "--client-port", "--client-pfx-path", "--client-pfx-password",
        "--client-endpoint-host", "--ffmpeg-path",
    ];

    public static int Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("setup is for Linux. On Windows, install with the MSI or install-node.ps1.");
            return 1;
        }
        if (!LinuxSetup.IsRoot)
        {
            Console.Error.WriteLine("Run setup as root: sudo larisvms-node setup ...");
            return 1;
        }

        var parsed = SetupArgs.Parse(args, Options, Flags);
        if (parsed.Error is { } parseError)
        {
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var registered = File.Exists(NodeConfigStore.FilePath);
        if (parsed.Has("--reregister") && registered)
        {
            File.Delete(NodeConfigStore.FilePath);
            registered = false;
            Console.WriteLine("Forgot this node's registration; it registers again when the service restarts.");
        }

        var serverUrl = parsed.Value("--server-url");
        var registrationKey = parsed.Value("--registration-key");
        if (!registered && (serverUrl is null || registrationKey is null))
        {
            Console.Error.WriteLine("This node isn't registered yet: give --server-url and --registration-key.");
            return 1;
        }
        if (registered && registrationKey is not null)
        {
            Console.Error.WriteLine("This node is already registered. Add --reregister to register it again with the new key.");
            return 1;
        }
        if (parsed.ValidateCommon() is { } invalid)
        {
            Console.Error.WriteLine(invalid);
            return 1;
        }
        if (!parsed.TryPort("--live-port", out var livePort, out var portError) || !parsed.TryPort("--client-port", out var clientPort, out portError))
        {
            Console.Error.WriteLine(portError);
            return 1;
        }

        void Log(string message) => Console.WriteLine(message);

        LinuxSetup.WriteEnvFile("node", new Dictionary<string, string?>
        {
            ["LARISVMS_SERVER_URL"] = serverUrl,
            ["LARISVMS_SERVER_CERT_THUMBPRINT"] = parsed.Value("--server-cert-thumbprint"),
            ["LARISVMS_STORAGE_ROOT"] = parsed.Value("--storage-root"),
            ["LARISVMS_ARCHIVE_ROOT"] = parsed.Value("--archive-root"),
            ["LARISVMS_LIVE_PORT"] = livePort?.ToString(CultureInfo.InvariantCulture),
            ["LARISVMS_FFMPEG_PATH"] = parsed.Value("--ffmpeg-path"),
        }.Where(kv => kv.Value is not null).ToDictionary());
        Log($"Wrote {LinuxSetup.EnvFilePath("node")}.");

        if (registrationKey is not null)
        {
            LinuxSetup.WriteServiceFile(RegistrationKeyFile.Path, registrationKey);
            Log("Saved the registration key for the service's first start.");
        }

        foreach (var folder in new[] { parsed.Value("--storage-root"), parsed.Value("--archive-root") })
        {
            if (folder is not null) LinuxSetup.PrepareServiceDirectory(folder, Log);
        }

        if (clientPort is not null)
        {
            var endpoint = new Dictionary<string, object?>
            {
                ["enabled"] = true,
                ["port"] = clientPort,
                ["allowInsecure"] = parsed.Has("--client-allow-insecure"),
                ["pfxPath"] = parsed.Value("--client-pfx-path"),
                ["pfxPassword"] = parsed.Value("--client-pfx-password"),
                ["host"] = parsed.Value("--client-endpoint-host"),
            }.Where(kv => kv.Value is not null).ToDictionary();
            LinuxSetup.WriteServiceFile(ClientEndpointConfig.LocalConfigPath, JsonSerializer.Serialize(endpoint));
            Log($"Wrote {ClientEndpointConfig.LocalConfigPath}.");
            if (parsed.Value("--client-pfx-path") is { } pfx)
                Log($"The larisvms user must be able to read {pfx}.");
        }

        var liveAllowFrom = (parsed.Value("--live-allow-from") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        LinuxSetup.OpenFirewallPort("node", livePort ?? CurrentLivePort(), "tcp", liveAllowFrom, Log);
        if (clientPort is not null) LinuxSetup.OpenFirewallPort("node", clientPort.Value, "tcp", [], Log);

        if (!LinuxSetup.RestartService("larisvms-node", Log))
        {
            Console.Error.WriteLine("Could not restart larisvms-node; see systemctl status larisvms-node.");
            return 1;
        }
        Log("larisvms-node restarted. Follow it with: journalctl -u larisvms-node -f");
        if (registrationKey is not null) Log("Then approve the node on the server's Nodes page.");
        return 0;
    }

    /// <summary>The live port already configured, for a setup run that only changes other settings.</summary>
    private static int CurrentLivePort()
    {
        var path = LinuxSetup.EnvFilePath("node");
        var configured = File.Exists(path) ? LinuxSetup.ReadEnv(File.ReadAllText(path), "LARISVMS_LIVE_PORT") : null;
        return int.TryParse(configured, CultureInfo.InvariantCulture, out var port) ? port : 8554;
    }
}
