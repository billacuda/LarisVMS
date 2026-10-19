using System.Net;
using System.Text.RegularExpressions;

namespace LarisVMS.Core.Security;

/// <summary>Names a node or proxy reports for itself at registration: a DNS hostname or an IP
/// address. They end up in page markup, confirm prompts, and the Content-Security-Policy header, so
/// anything else is refused rather than escaped everywhere it's used.</summary>
public static partial class HostNames
{
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9_]([A-Za-z0-9_-]{0,62})(\.[A-Za-z0-9_]([A-Za-z0-9_-]{0,62}))*\.?$")]
    private static partial Regex DnsName();

    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && (DnsName().IsMatch(name) || IPAddress.TryParse(name, out _));
}
