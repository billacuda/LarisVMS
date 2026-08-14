using System.Reflection;

namespace LarisVMS.Node;

/// <summary>
/// The version this node reports on registration and every heartbeat, read from the build's own
/// assembly metadata (driven by LarisVMS.Node.csproj's &lt;Version&gt;) rather than a hand-maintained
/// string literal — a literal is exactly what let this drift two releases behind the actual build
/// for a while (see CHANGELOG).
/// </summary>
public static class NodeVersion
{
    public static readonly string Current = Resolve();

    private static string Resolve()
    {
        var raw = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(raw)) return "unknown";

        // The SDK appends "+<git-commit-sha>" to InformationalVersion by default when building
        // inside a git repo (source revision embedding) — real and useful for a build artifact's own
        // metadata, but not what should show up as this node's version in Admin -> Nodes. Strip it.
        var plus = raw.IndexOf('+');
        return plus < 0 ? raw : raw[..plus];
    }
}
