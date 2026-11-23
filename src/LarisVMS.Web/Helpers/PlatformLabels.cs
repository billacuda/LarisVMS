using LarisVMS.Core;

namespace LarisVMS.Web.Helpers;

/// <summary>Display names for node and proxy platforms ("linux-x64" → "Linux x64"). Product names,
/// so not translated. A null platform is a node or proxy registered before platforms were reported,
/// which is a Windows one.</summary>
public static class PlatformLabels
{
    public static string Display(string? platform)
    {
        var os = PlatformId.OsOf(platform ?? PlatformId.Legacy);
        var dash = os.IndexOf('-');
        var (name, arch) = dash < 0 ? (os, "") : (os[..dash], os[(dash + 1)..]);
        var osName = name switch
        {
            "win" => "Windows",
            "linux" => "Linux",
            "osx" => "macOS",
            _ => name,
        };
        return arch.Length == 0 ? osName : $"{osName} {arch}";
    }
}
