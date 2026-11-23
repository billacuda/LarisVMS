namespace LarisVMS.Core;

/// <summary>
/// How to compare paths on this machine's own file system: ignoring case on Windows, where
/// <c>D:\Rec</c> and <c>d:\rec</c> are the same folder (and UNC paths often come back in a different
/// case), and exactly on Linux, where they are two folders. Only for local paths — a path that came
/// from another machine follows that machine's rules (see the server's handling of node paths).
/// </summary>
public static class PathComparer
{
    public static StringComparer Local { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison LocalComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
