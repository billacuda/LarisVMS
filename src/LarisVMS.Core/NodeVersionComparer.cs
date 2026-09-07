namespace LarisVMS.Core;

/// <summary>
/// Mirrors dploid's AgentController.IsNewerVersion private helper — plain System.Version comparison.
/// Both LarisVMS.Node's reported version and NodeBuildVersion.Version are "major.minor.patch"
/// (hand-maintained semver) optionally followed by a monotonic build number as a 4th component
/// (0.188.0.244 — appended at publish time by build-node.ps1 so a rebuild with an unchanged semver
/// still registers as newer). System.Version orders 2/3/4-part versions correctly, and there are no
/// semver pre-release suffixes anywhere in this codebase's scheme (see NodeVersion.Resolve, which
/// strips the SDK's own "+&lt;gitsha&gt;" suffix before this ever sees the string). A node with no
/// reported version at all — a fresh
/// registration whose first heartbeat hasn't landed yet, or an ancient build predating
/// AssemblyInformationalVersionAttribute — always treats any uploaded build as newer, same as
/// dploid's null-current handling: there's nothing to compare against, and refusing to update it
/// would leave it stuck.
/// </summary>
public static class NodeVersionComparer
{
    public static bool IsNewer(string latestVersion, string? currentVersion)
    {
        if (string.IsNullOrEmpty(currentVersion)) return true;

        return Version.TryParse(latestVersion, out var latest) &&
               Version.TryParse(currentVersion, out var current) &&
               latest > current;
    }
}
