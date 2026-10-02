using System.Collections.Generic;

namespace LarisVMS.Installer
{
    /// <summary>Per-MSI facts the custom actions need. Each .wxs sets LARIS_PRODUCT to one of the keys
    /// below; everything else (service name, which properties are remembered, defaults) comes from
    /// here so the three installers can't drift apart in how they treat the same setting.</summary>
    internal sealed class Product
    {
        public string Key { get; private set; } = "";
        public string ServiceName { get; private set; } = "";
        public string ExeName { get; private set; } = "";
        /// <summary>Non-secret properties persisted to HKLM\SOFTWARE\LarisVMS\{Key} so an upgrade
        /// needs no parameters. Secrets (keys, passwords) are never stored.</summary>
        public string[] Remembered { get; private set; } = new string[0];
        public Dictionary<string, string> Defaults { get; private set; } = new Dictionary<string, string>();
        /// <summary>Firewall rules the PowerShell installer created; removed when the MSI takes over
        /// a script install so the MSI's own rules don't end up duplicated.</summary>
        public string[] LegacyFirewallRules { get; private set; } = new string[0];
        /// <summary>The DPAPI registration file that means "already registered" (node/proxy only).</summary>
        public string? RegistrationFile { get; private set; }

        public string RegistryKey => @"SOFTWARE\LarisVMS\" + Key;

        public static Product Get(string key)
        {
            switch (key)
            {
                case "Web":
                    return new Product
                    {
                        Key = "Web",
                        ServiceName = "LarisVMSWeb",
                        ExeName = "LarisVMS.Web.exe",
                        Remembered = new[] { "HTTPSPORT", "SERVICEACCOUNT" },
                        Defaults = new Dictionary<string, string> { ["HTTPSPORT"] = "8444" },
                        LegacyFirewallRules = new[] { "LarisVMS Web" },
                    };
                case "Node":
                    return new Product
                    {
                        Key = "Node",
                        ServiceName = "LarisVMSNode",
                        ExeName = "LarisVMS.Node.exe",
                        Remembered = new[]
                        {
                            "SERVERURL", "STORAGEROOT", "ARCHIVEROOT", "FFMPEGPATH", "LIVEPORT", "INSECURETLS",
                            "CLIENTPORT", "CLIENTENDPOINTHOST", "CLIENTPFXPATH", "CLIENTALLOWINSECURE", "SERVICEACCOUNT",
                        },
                        Defaults = new Dictionary<string, string> { ["LIVEPORT"] = "8554" },
                        LegacyFirewallRules = new[] { "LarisVMS Node Live View", "LarisVMS Node Client Endpoint" },
                        RegistrationFile = "node.config",
                    };
                case "Proxy":
                    return new Product
                    {
                        Key = "Proxy",
                        ServiceName = "LarisVMSProxy",
                        ExeName = "LarisVMS.Proxy.exe",
                        Remembered = new[]
                        {
                            "SERVERURL", "INSECURETLS", "CLIENTPORT", "CLIENTENDPOINTHOST", "CLIENTPFXPATH",
                            "CLIENTALLOWINSECURE", "SERVICEACCOUNT",
                        },
                        Defaults = new Dictionary<string, string> { ["CLIENTPORT"] = "4443" },
                        LegacyFirewallRules = new[] { "LarisVMS Proxy Endpoint" },
                        RegistrationFile = "proxy.config",
                    };
                default:
                    throw new System.ArgumentException("Unknown LARIS_PRODUCT '" + key + "'");
            }
        }
    }
}
