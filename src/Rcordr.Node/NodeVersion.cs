using System.Reflection;

namespace Rcordr.Node;

/// <summary>
/// The version this node reports on registration and every heartbeat, read from the build's own
/// assembly metadata (driven by Rcordr.Node.csproj's &lt;Version&gt;) rather than a hand-maintained
/// string literal — a literal is exactly what let this drift two releases behind the actual build
/// for a while (see CHANGELOG).
/// </summary>
public static class NodeVersion
{
    public static readonly string Current =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";
}
