// LarisVMS.NodeUpdater compiles this same file (it references no other project) under its own
// namespace, so a project that references both (the tests) doesn't see two LarisVMS.Core.LarisPaths.
#if LARISVMS_NODEUPDATER
namespace LarisVMS.NodeUpdater.Shared;
#else
namespace LarisVMS.Core;
#endif

/// <summary>
/// Where LarisVMS keeps its machine-wide files, per OS. Every service (Web, Node, Proxy, the Vision
/// Service, and the updater) resolves its data, log, and config folders here, so a Linux install
/// never ends up under <c>/usr/share</c> (what <see cref="Environment.SpecialFolder.CommonApplicationData"/>
/// returns there) and Windows keeps exactly the folders it always used.
///
/// <list type="table">
///   <listheader><term>Folder</term><description>Windows / Linux</description></listheader>
///   <item><term><see cref="DataRoot"/></term><description><c>%ProgramData%\LarisVMS</c> / <c>/var/lib/larisvms</c></description></item>
///   <item><term><see cref="LogRoot"/></term><description><c>%ProgramData%\LarisVMS\logs</c> / <c>/var/log/larisvms</c></description></item>
///   <item><term><see cref="ConfigRoot"/></term><description>null (config sits beside the program) / <c>/etc/larisvms</c></description></item>
/// </list>
///
/// Each can be moved with an environment variable (<c>LARISVMS_DATA_DIR</c>, <c>LARISVMS_LOG_DIR</c>,
/// <c>LARISVMS_CONFIG_DIR</c>), for example to run a from-source build as an ordinary user. A moved
/// data folder doesn't move the log folder on Windows unless that is set too.
///
/// This file is also compiled into LarisVMS.NodeUpdater, which references no other project.
/// </summary>
public static class LarisPaths
{
    public const string DataDirVariable = "LARISVMS_DATA_DIR";
    public const string LogDirVariable = "LARISVMS_LOG_DIR";
    public const string ConfigDirVariable = "LARISVMS_CONFIG_DIR";

    /// <summary>Machine-wide data: keys, certificates, registration and config stores, update
    /// staging, models, and (by default) a node's recordings.</summary>
    public static string DataRoot { get; } = Resolve(DataDirVariable, DefaultDataRoot);

    /// <summary>Service log files.</summary>
    public static string LogRoot { get; } = Resolve(LogDirVariable,
        () => OperatingSystem.IsWindows() ? Path.Combine(DataRoot, "logs") : "/var/log/larisvms");

    /// <summary>Operator-edited configuration (the Web server's appsettings.Production.json and
    /// setup-generated.json, the services' .env files). Null on Windows, where those files stay
    /// beside the program as they always have.</summary>
    public static string? ConfigRoot { get; } = Environment.GetEnvironmentVariable(ConfigDirVariable) is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : OperatingSystem.IsWindows() ? null : "/etc/larisvms";

    /// <summary>An operator-edited config file: in <see cref="ConfigRoot"/> when there is one, else
    /// beside the program in <paramref name="programDirectory"/> (Windows).</summary>
    public static string ConfigFile(string programDirectory, string fileName) =>
        Path.Combine(ConfigRoot ?? programDirectory, fileName);

    /// <summary>A folder the program fills at runtime (a download cache): beside the program on
    /// Windows, where it has always been, and under <see cref="DataRoot"/> elsewhere, where the
    /// install folder is read-only to the service.</summary>
    public static string RuntimeFolder(string programDirectory, string name) => OperatingSystem.IsWindows()
        ? Path.Combine(programDirectory, name)
        : Path.Combine(DataRoot, name);

    private static string DefaultDataRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LarisVMS")
        : "/var/lib/larisvms";

    private static string Resolve(string variable, Func<string> fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : fallback();
}
