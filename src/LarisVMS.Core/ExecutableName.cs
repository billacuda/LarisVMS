// LarisVMS.NodeUpdater compiles this same file (it references no other project) under its own
// namespace, so a project that references both (the tests) doesn't see the type twice.
#if LARISVMS_NODEUPDATER
namespace LarisVMS.NodeUpdater.Shared;
#else
namespace LarisVMS.Core;
#endif

/// <summary>
/// File names of LarisVMS's own programs, which end in ".exe" only on Windows
/// ("LarisVMS.Node.exe" there, "LarisVMS.Node" on Linux).
/// </summary>
public static class ExecutableName
{
    public const string Node = "LarisVMS.Node";
    public const string Proxy = "LarisVMS.Proxy";
    public const string VisionService = "LarisVMS.Vision.Service";
    public const string Updater = "LarisVMS.NodeUpdater";

    /// <summary>The program's file name on this machine.</summary>
    public static string For(string program) => OperatingSystem.IsWindows() ? program + ".exe" : program;

    /// <summary>The program's file name on a machine running <paramref name="platform"/> — for the
    /// server, which serves both Windows and Linux builds.</summary>
    public static string For(string program, string platform) => PlatformId.IsWindows(platform) ? program + ".exe" : program;
}
