using System.Reflection;

namespace LarisVMS.Proxy;

/// <summary>The version this relay reports on registration and every heartbeat, read from the build's
/// own assembly metadata (LarisVMS.Proxy.csproj's &lt;Version&gt;) — the same shape as NodeVersion.</summary>
public static class ProxyVersion
{
    public static readonly string Current = Resolve();

    private static string Resolve()
    {
        var raw = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(raw)) return "unknown";
        var plus = raw.IndexOf('+');
        return plus < 0 ? raw : raw[..plus];
    }
}
