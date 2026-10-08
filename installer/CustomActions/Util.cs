using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace LarisVMS.Installer
{
    internal static class Util
    {
        public static string ProgramDataLaris =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS");

        /// <summary>Same quoting as install-node.ps1's Format-ServiceArg: wrap in quotes when the value
        /// has whitespace or a quote, escaping embedded quotes.</summary>
        public static string QuoteArg(string value)
        {
            if (value.Length == 0) return "\"\"";
            if (value.Any(char.IsWhiteSpace) || value.Contains("\""))
                return "\"" + value.Replace("\"", "\\\"") + "\"";
            return value;
        }

        /// <summary>Splits a service ImagePath into its exe and arguments, honouring quotes.</summary>
        public static List<string> SplitCommandLine(string commandLine)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false, any = false;
            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"') { current.Append('"'); i++; any = true; }
                else if (c == '"') { inQuotes = !inQuotes; any = true; }
                else if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                }
                else { current.Append(c); any = true; }
            }
            if (any) result.Add(current.ToString());
            return result;
        }

        public static string JsonString(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Reads a top-level-ish string property from a small JSON file without a JSON
        /// library (none ships with .NET Framework). Good enough for the flat files LarisVMS writes.</summary>
        public static string? JsonReadString(string json, string name)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        /// <summary>Trims whitespace and one pair of surrounding quotes from a typed path (Explorer's
        /// "Copy as path" adds them).</summary>
        public static string CleanPath(string? path)
        {
            var p = (path ?? "").Trim();
            if (p.Length >= 2 && p[0] == '"' && p[p.Length - 1] == '"') p = p.Substring(1, p.Length - 2).Trim();
            return p;
        }

        /// <summary>Opens a .pfx the way the services will, so a wrong password or unreadable file is
        /// caught in the dialog instead of when the service starts.</summary>
        public static string? CheckPfx(string path, string? password, string pathProperty, string passwordProperty)
        {
            if (!File.Exists(path))
                return "Certificate file not found or not readable from this account: " + path + " (property " + pathProperty + ").";
            try
            {
#pragma warning disable SYSLIB0057 // X509CertificateLoader doesn't exist on net472; also compiled into the net10 tests
                using (new X509Certificate2(path, password ?? "", X509KeyStorageFlags.EphemeralKeySet)) { }
#pragma warning restore SYSLIB0057
                return null;
            }
            catch (CryptographicException ex)
            {
                return "Could not open " + path + " — " + ex.Message.Trim() + " Check the certificate password (property " + passwordProperty + ").";
            }
        }

        /// <summary>The <c>Kestrel:Certificates:Default</c> object of an appsettings file (the only
        /// "Default" key whose value is an object), so its Path/Password aren't confused with any
        /// other "Path"/"Password" key in the file. Null when there's no such block.</summary>
        public static Match? CertBlock(string json)
        {
            var m = Regex.Match(json, "\"Default\"\\s*:\\s*\\{[^{}]*\\}");
            return m.Success ? m : null;
        }

        public sealed class EndpointConfig
        {
            public string Port { get; set; } = "";
            public string? Host { get; set; }
            public string? PfxPath { get; set; }
            public bool AllowInsecure { get; set; }
        }

        /// <summary>Reads the settings a node's client-endpoint.json or a proxy's proxy-endpoint.json
        /// holds (as WriteEndpointConfig writes them). Null when there's no valid port, or when
        /// <paramref name="requireEnabled"/> and the node's endpoint is switched off.</summary>
        public static EndpointConfig? ReadEndpointConfig(string json, bool requireEnabled)
        {
            if (requireEnabled && !Regex.IsMatch(json, "\"enabled\"\\s*:\\s*true", RegexOptions.IgnoreCase)) return null;
            var port = Regex.Match(json, "\"port\"\\s*:\\s*(\\d+)", RegexOptions.IgnoreCase);
            if (!port.Success || !IsPort(port.Groups[1].Value)) return null;

            return new EndpointConfig
            {
                Port = port.Groups[1].Value,
                Host = NullIfEmpty(JsonReadString(json, "host")),
                PfxPath = NullIfEmpty(JsonReadString(json, "pfxPath")),
                AllowInsecure = Regex.IsMatch(json, "\"allowInsecure\"\\s*:\\s*true", RegexOptions.IgnoreCase),
            };
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

        public static bool IsLocalSystem(string? account) =>
            string.IsNullOrWhiteSpace(account)
            || account!.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || account.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || account.Equals(@".\LocalSystem", StringComparison.OrdinalIgnoreCase);

        /// <summary>Accounts that run without a password: the built-in service identities and group
        /// managed service accounts (name ends with $).</summary>
        public static bool NeedsPassword(string? account) =>
            !IsLocalSystem(account)
            && !account!.StartsWith(@"NT AUTHORITY\", StringComparison.OrdinalIgnoreCase)
            && !account.EndsWith("$", StringComparison.Ordinal);

        public static int Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false };
            using (var p = Process.Start(psi)!)
            {
                p.WaitForExit(60000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }

        public static bool IsPort(string? value) =>
            int.TryParse(value, out var port) && port >= 1 && port <= 65535;
    }
}
