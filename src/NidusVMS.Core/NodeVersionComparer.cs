namespace NidusVMS.Core;

/// <summary>
/// Mirrors dploid's AgentController.IsNewerVersion private helper — plain System.Version comparison,
/// since both NidusVMS.Node's reported version and NodeBuildVersion.Version are compiled-in
/// "major.minor.patch" strings with no semver pre-release suffixes anywhere in this codebase's
/// versioning scheme (see NodeVersion.Resolve, which already strips the SDK's own "+&lt;gitsha&gt;"
/// suffix before this ever sees the string). A node with no reported version at all — a fresh
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
