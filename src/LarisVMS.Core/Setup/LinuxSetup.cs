using System.Diagnostics;
using System.Text;

namespace LarisVMS.Core.Setup;

/// <summary>
/// What <c>larisvms-node setup</c> and <c>larisvms-proxy setup</c> share on Linux: the env file the
/// systemd unit reads (<c>/etc/larisvms/&lt;kind&gt;.env</c>), files handed to the service user, the
/// firewall, and restarting the unit. They run as root (sudo) and leave the service running as the
/// unprivileged <see cref="ServiceUser"/>, which the packages create.
/// </summary>
public static class LinuxSetup
{
    public const string ServiceUser = "larisvms";

    /// <summary>The unit's EnvironmentFile.</summary>
    public static string EnvFilePath(string kind) => Path.Combine(LarisPaths.ConfigRoot ?? "/etc/larisvms", kind + ".env");

    /// <summary>Ports setup opened, one "port/proto" per line, so the package's removal script can
    /// close them again.</summary>
    public static string FirewallRecordPath(string kind) => Path.Combine(LarisPaths.ConfigRoot ?? "/etc/larisvms", kind + ".firewall");

    /// <summary>Whether this process can do setup's work (root).</summary>
    public static bool IsRoot => Environment.IsPrivilegedProcess;

    /// <summary>Sets <paramref name="values"/> in an env file's text, keeping its comments and every
    /// other line. A null value removes the key.</summary>
    public static string MergeEnv(string? existing, IReadOnlyDictionary<string, string?> values)
    {
        var lines = (existing ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var pending = new Dictionary<string, string?>(values, StringComparer.Ordinal);

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var key = KeyOf(lines[i]);
            if (key is null || !pending.TryGetValue(key, out var value)) continue;
            if (value is null) lines.RemoveAt(i);
            else lines[i] = $"{key}={Quote(value)}";
            pending.Remove(key);
        }
        foreach (var (key, value) in pending)
        {
            if (value is not null) lines.Add($"{key}={Quote(value)}");
        }
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The value of <paramref name="key"/> in an env file's text, unquoted, or null.</summary>
    public static string? ReadEnv(string? text, string key)
    {
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (KeyOf(line) != key) continue;
            var raw = line[(line.IndexOf('=') + 1)..].Trim();
            return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"'
                ? raw[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\")
                : raw;
        }
        return null;
    }

    private static string? KeyOf(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#') return null;
        var eq = trimmed.IndexOf('=');
        return eq > 0 ? trimmed[..eq].Trim() : null;
    }

    /// <summary>systemd's EnvironmentFile quoting: double quotes, with backslash and quote escaped.
    /// Line breaks aren't allowed in a value.</summary>
    internal static string Quote(string value)
    {
        if (value.Contains('\n') || value.Contains('\r')) throw new ArgumentException("A setting can't contain a line break.", nameof(value));
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Writes the env file (root-owned, readable by the service's group).</summary>
    public static void WriteEnvFile(string kind, IReadOnlyDictionary<string, string?> values)
    {
        var path = EnvFilePath(kind);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path) ? File.ReadAllText(path) : null;
        WriteAtomically(path, MergeEnv(existing, values));
        Run("chown", ["root:" + ServiceUser, path], log: null);
        SetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    }

    /// <summary>Writes a file only the service user can read (a registration key, an endpoint config).</summary>
    public static void WriteServiceFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomically(path, content);
        SetMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        GiveToServiceUser(path);
    }

    /// <summary>Creates <paramref name="directory"/> if needed and hands it to the service user (a
    /// recordings folder). An existing folder's contents are left alone.</summary>
    public static void PrepareServiceDirectory(string directory, Action<string> log)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            log($"Created {directory}.");
        }
        GiveToServiceUser(directory);
        SetMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static bool GiveToServiceUser(string path) => Run("chown", [ServiceUser + ":" + ServiceUser, path], log: null);

    /// <summary>Opens <paramref name="port"/> in firewalld or ufw, whichever is active (nothing when
    /// neither is), limited to <paramref name="allowFrom"/> when given, and records it for removal.</summary>
    public static void OpenFirewallPort(string kind, int port, string protocol, IReadOnlyList<string> allowFrom, Action<string> log)
    {
        var opened = false;
        if (Run("firewall-cmd", ["--state"], log: null))
        {
            if (allowFrom.Count == 0)
                opened = Run("firewall-cmd", ["--permanent", $"--add-port={port}/{protocol}"], log);
            else
                opened = allowFrom.All(source => Run("firewall-cmd", ["--permanent",
                    $"--add-rich-rule=rule family=\"{(source.Contains(':') ? "ipv6" : "ipv4")}\" source address=\"{source}\" port port=\"{port}\" protocol=\"{protocol}\" accept"], log));
            Run("firewall-cmd", ["--reload"], log);
        }
        else if (RunCapture("ufw", ["status"]) is { } status && status.Contains("Status: active", StringComparison.Ordinal))
        {
            opened = allowFrom.Count == 0
                ? Run("ufw", ["allow", $"{port}/{protocol}"], log)
                : allowFrom.All(source => Run("ufw", ["allow", "from", source, "to", "any", "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "proto", protocol], log));
        }
        else
        {
            log($"No active firewalld or ufw: nothing to open for {port}/{protocol}.");
            return;
        }

        if (!opened)
        {
            log($"Could not open {port}/{protocol} in the firewall; open it by hand.");
            return;
        }
        log(allowFrom.Count == 0 ? $"Opened {port}/{protocol} in the firewall." : $"Opened {port}/{protocol} in the firewall for {string.Join(", ", allowFrom)}.");
        var record = FirewallRecordPath(kind);
        var entry = $"{port}/{protocol}";
        var lines = File.Exists(record) ? File.ReadAllLines(record).ToList() : [];
        if (!lines.Contains(entry)) lines.Add(entry);
        File.WriteAllLines(record, lines);
    }

    /// <summary>Enables the unit and restarts it so it starts with the new settings.</summary>
    public static bool RestartService(string unit, Action<string> log) =>
        Run("systemctl", ["enable", unit], log) && Run("systemctl", ["restart", unit], log);

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        SetMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    private static bool Run(string file, IEnumerable<string> arguments, Action<string>? log) =>
        RunCapture(file, arguments, log) is not null;

    /// <summary>Standard output when the command exits 0; null when it fails or doesn't exist.</summary>
    private static string? RunCapture(string file, IEnumerable<string> arguments, Action<string>? log = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000)) return null;
            if (process.ExitCode == 0) return stdout;
            log?.Invoke($"{file} {string.Join(' ', psi.ArgumentList)} failed: {stderr.Trim()}");
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
