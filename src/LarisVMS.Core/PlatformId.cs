using System.Runtime.InteropServices;

// LarisVMS.NodeUpdater compiles this same file (it references no other project) under its own
// namespace, so a project that references both (the tests) doesn't see the type twice.
#if LARISVMS_NODEUPDATER
namespace LarisVMS.NodeUpdater.Shared;
#else
namespace LarisVMS.Core;
#endif

/// <summary>
/// The platform a node or proxy build is for, as a .NET runtime identifier: "win-x64", "linux-x64",
/// later "linux-arm64". A node or proxy reports <see cref="Current"/> when it registers; the server
/// keeps one approved build per platform and offers each machine the build for its own platform,
/// which is what lets Windows and Linux nodes and proxies share one server. Update signatures cover
/// the platform string, so the server's and the machine's values must agree exactly.
///
/// Proxy builds are registered under <see cref="ForProxy"/> ("proxy-win-x64") so they never mix with
/// node builds of the same OS.
/// </summary>
public static class PlatformId
{
    public const string WindowsX64 = "win-x64";
    public const string LinuxX64 = "linux-x64";
    public const string LinuxArm64 = "linux-arm64";

    /// <summary>A node or proxy registered before platforms were reported is a Windows x64 one.</summary>
    public const string Legacy = WindowsX64;

    private const string ProxyPrefix = "proxy-";

    public static string Current { get; } = For(
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "unknown",
        RuntimeInformation.OSArchitecture);

    internal static string For(string os, Architecture architecture) => $"{os}-{architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        _ => architecture.ToString().ToLowerInvariant(),
    }}";

    /// <summary>The build platform for a proxy on <paramref name="platform"/>: "proxy-linux-x64".
    /// Already-prefixed values pass through.</summary>
    public static string ForProxy(string platform) =>
        platform.StartsWith(ProxyPrefix, StringComparison.OrdinalIgnoreCase) ? platform : ProxyPrefix + platform;

    /// <summary>The OS platform behind a node or proxy build platform ("proxy-linux-x64" → "linux-x64").</summary>
    public static string OsOf(string platform) =>
        platform.StartsWith(ProxyPrefix, StringComparison.OrdinalIgnoreCase) ? platform[ProxyPrefix.Length..] : platform;

    public static bool IsWindows(string platform) => OsOf(platform).StartsWith("win", StringComparison.OrdinalIgnoreCase);

    /// <summary>The platforms a Web package bundles node and proxy builds for.</summary>
    public static IReadOnlyList<string> Bundled { get; } = [WindowsX64, LinuxX64];

    /// <summary>A platform a node or proxy reported at registration, if it's a well-formed runtime
    /// identifier ("linux-x64"); null otherwise, which is then treated as <see cref="Legacy"/>. Stops a
    /// registering machine from storing arbitrary text that the update path later matches on.</summary>
    public static string? Sanitize(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return null;
        var value = reported.Trim().ToLowerInvariant();
        if (value.Length > 50) return null;
        var dash = value.IndexOf('-');
        if (dash <= 0 || dash == value.Length - 1 || value.IndexOf('-', dash + 1) >= 0) return null;
        return value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? value : null;
    }
}
