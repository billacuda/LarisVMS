namespace LarisVMS.Tests;

/// <summary>Lets a test written with Windows paths run on Linux too: on Windows the path is returned
/// as is; elsewhere <c>E:\rec\cam-1</c> becomes <c>/e/rec/cam-1</c> and <c>\\nas\rec</c> becomes
/// <c>/nas/rec</c>, the shape a node's own paths have there.</summary>
internal static class TestPaths
{
    public static string Native(string windowsPath)
    {
        if (OperatingSystem.IsWindows()) return windowsPath;
        var p = windowsPath;
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
            p = "/" + char.ToLowerInvariant(p[0]) + p[2..];
        else if (p.StartsWith(@"\\", StringComparison.Ordinal))
            p = p[1..];
        return p.Replace('\\', '/');
    }
}
