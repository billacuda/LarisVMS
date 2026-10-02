using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
