using System.Text.Json;
using LarisVMS.Core.Security;
using LarisVMS.Core.Setup;

namespace LarisVMS.Proxy;

/// <summary>
/// <c>sudo larisvms-proxy setup ...</c>: pairs a Linux media proxy installed from the .deb or .rpm
/// with a server — the Linux counterpart of the proxy MSI and install-proxy.ps1. It writes
/// <c>/etc/larisvms/proxy.env</c>, the one-time registration key, and <c>proxy-endpoint.json</c>,
/// opens the client port in the firewall, and restarts the service, which registers on its next start.
/// </summary>
internal static class ProxySetupCommand
{
    public const string Usage = """
        Usage: sudo larisvms-proxy setup --server-url <url> --registration-key <key> [options]

          --server-url <url>               The LarisVMS server, e.g. https://vms.example.com:8444
          --registration-key <key>         From the server's Media proxies page (first setup only)
          --server-cert-thumbprint <hex>   Pin the server certificate's SHA-256 thumbprint before registering
                                           (Nodes page > Updates and registration > Server certificate)
          --client-port <port>             HTTPS port browsers reach (default 4443)
          --client-pfx-path <path>         Certificate for it (default: self-signed)
          --client-pfx-password <secret>
          --client-allow-insecure          Allow plain HTTP there
          --client-endpoint-host <name>    The host name browsers use for it
          --reregister                     Forget the current registration and register again

        Run again with any of these to change them; the service restarts with the new settings.
        """;

    private static readonly HashSet<string> Flags = ["--reregister", "--client-allow-insecure"];

    private static readonly HashSet<string> Options =
    [
        "--server-url", "--registration-key", "--server-cert-thumbprint", "--client-port", "--client-pfx-path",
        "--client-pfx-password", "--client-endpoint-host",
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
            Console.Error.WriteLine("setup is for Linux. On Windows, install with the MSI or install-proxy.ps1.");
            return 1;
        }
        if (!LinuxSetup.IsRoot)
        {
            Console.Error.WriteLine("Run setup as root: sudo larisvms-proxy setup ...");
            return 1;
        }

        var parsed = SetupArgs.Parse(args, Options, Flags);
        if (parsed.Error is { } parseError)
        {
            Console.Error.WriteLine(parseError);
            Console.Error.WriteLine(Usage);
            return 1;
        }

        var registered = File.Exists(ProxyConfigStore.FilePath);
        if (parsed.Has("--reregister") && registered)
        {
            File.Delete(ProxyConfigStore.FilePath);
            registered = false;
            Console.WriteLine("Forgot this proxy's registration; it registers again when the service restarts.");
        }

        var serverUrl = parsed.Value("--server-url");
        var registrationKey = parsed.Value("--registration-key");
        if (!registered && (serverUrl is null || registrationKey is null))
        {
            Console.Error.WriteLine("This proxy isn't registered yet: give --server-url and --registration-key.");
            return 1;
        }
        if (registered && registrationKey is not null)
        {
            Console.Error.WriteLine("This proxy is already registered. Add --reregister to register it again with the new key.");
            return 1;
        }
        if (parsed.ValidateCommon() is { } invalid)
        {
            Console.Error.WriteLine(invalid);
            return 1;
        }
        if (!parsed.TryPort("--client-port", out var clientPort, out var portError))
        {
            Console.Error.WriteLine(portError);
            return 1;
        }

        void Log(string message) => Console.WriteLine(message);

        LinuxSetup.WriteEnvFile("proxy", new Dictionary<string, string?>
        {
            ["LARISVMS_SERVER_URL"] = serverUrl,
            ["LARISVMS_SERVER_CERT_THUMBPRINT"] = parsed.Value("--server-cert-thumbprint"),
        }.Where(kv => kv.Value is not null).ToDictionary());
        Log($"Wrote {LinuxSetup.EnvFilePath("proxy")}.");

        if (registrationKey is not null)
        {
            LinuxSetup.WriteServiceFile(RegistrationKeyFile.Path, registrationKey);
            Log("Saved the registration key for the service's first start.");
        }

        // The endpoint file keeps what an earlier setup run gave unless this one changes it.
        var current = ProxyEndpointConfig.ReadLocal() ?? new ProxyEndpointLocalConfig();
        var endpoint = current with
        {
            Port = clientPort ?? (current.Port > 0 ? current.Port : ProxyEndpointConfig.DefaultPort),
            AllowInsecure = parsed.Has("--client-allow-insecure") || current.AllowInsecure,
            PfxPath = parsed.Value("--client-pfx-path") ?? current.PfxPath,
            PfxPassword = parsed.Value("--client-pfx-password") ?? current.PfxPassword,
            Host = parsed.Value("--client-endpoint-host") ?? current.Host,
        };
        LinuxSetup.WriteServiceFile(ProxyEndpointConfig.LocalConfigPath,
            JsonSerializer.Serialize(endpoint, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Log($"Wrote {ProxyEndpointConfig.LocalConfigPath} (port {endpoint.Port}).");
        if (endpoint.PfxPath is { } pfx) Log($"The larisvms user must be able to read {pfx}.");

        LinuxSetup.OpenFirewallPort("proxy", endpoint.Port, "tcp", [], Log);

        if (!LinuxSetup.RestartService("larisvms-proxy", Log))
        {
            Console.Error.WriteLine("Could not restart larisvms-proxy; see systemctl status larisvms-proxy.");
            return 1;
        }
        Log("larisvms-proxy restarted. Follow it with: journalctl -u larisvms-proxy -f");
        if (registrationKey is not null) Log("Then approve the proxy on the server's Media proxies page.");
        return 0;
    }
}
