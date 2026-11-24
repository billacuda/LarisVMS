using System.Text;
using System.Text.RegularExpressions;

namespace LarisVMS.Web.Helpers;

/// <summary>
/// The ready-to-paste Docker compose stack (for Portainer or <c>docker compose</c>) that the setup
/// wizard's Node step and the Media proxies page show beside the Windows and Linux install commands.
/// It matches <c>docker/compose.node.yml</c> and <c>docker/compose.proxy.yml</c> in the repository.
/// Every value is YAML single-quoted, since keys, URLs, and thumbprints can contain <c>:</c> or <c>#</c>.
/// </summary>
public static partial class DockerComposeSnippet
{
    public const string Registry = "ghcr.io/billacuda";

    /// <summary>The image tag for this server's own version ("0.223.0" from "0.223.0+abc123" or
    /// "0.223.0.245"), so a new node starts on the same release. "latest" when it can't be read.</summary>
    public static string ImageTag(string? runningVersion) =>
        runningVersion is not null && ReleaseVersion().Match(runningVersion) is { Success: true } m ? m.Value : "latest";

    public static string Node(string serverUrl, string registrationKey, string? thumbprint, string? runningVersion)
    {
        var tag = ImageTag(runningVersion);
        var sb = new StringBuilder();
        sb.AppendLine("services:");
        sb.AppendLine("  larisvms-node:");
        sb.AppendLine($"    image: {Registry}/larisvms-node:{tag}   # or {tag}-cuda / {tag}-tensorrt for NVIDIA GPUs");
        sb.AppendLine("    container_name: larisvms-node");
        sb.AppendLine("    hostname: larisvms-node-1   # the name this node shows under Nodes");
        sb.AppendLine("    network_mode: host");
        sb.AppendLine("    restart: unless-stopped");
        sb.AppendLine("    environment:");
        sb.AppendLine($"      LARISVMS_SERVER_URL: {Quote(serverUrl)}");
        sb.AppendLine($"      LARISVMS_REGISTRATION_KEY: {Quote(registrationKey)}");
        if (!string.IsNullOrEmpty(thumbprint))
            sb.AppendLine($"      LARISVMS_SERVER_CERT_THUMBPRINT: {Quote(thumbprint)}");
        sb.AppendLine("      LARISVMS_STORAGE_ROOT: '/recordings'");
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - larisvms-node-data:/var/lib/larisvms");
        sb.AppendLine("      - /srv/larisvms/recordings:/recordings   # where recordings go on this machine");
        sb.AppendLine("    # devices: ['/dev/dri:/dev/dri']   # Intel GPU (OpenVINO) and VAAPI video encoding");
        sb.AppendLine("volumes:");
        sb.Append("  larisvms-node-data:");
        return sb.ToString();
    }

    public static string Proxy(string serverUrl, string registrationKey, string? thumbprint, string? runningVersion, int clientPort = 4443)
    {
        var sb = new StringBuilder();
        sb.AppendLine("services:");
        sb.AppendLine("  larisvms-proxy:");
        sb.AppendLine($"    image: {Registry}/larisvms-proxy:{ImageTag(runningVersion)}");
        sb.AppendLine("    container_name: larisvms-proxy");
        sb.AppendLine("    hostname: larisvms-proxy-1");
        sb.AppendLine("    network_mode: host");
        sb.AppendLine("    restart: unless-stopped");
        sb.AppendLine("    environment:");
        sb.AppendLine($"      LARISVMS_SERVER_URL: {Quote(serverUrl)}");
        sb.AppendLine($"      LARISVMS_REGISTRATION_KEY: {Quote(registrationKey)}");
        if (!string.IsNullOrEmpty(thumbprint))
            sb.AppendLine($"      LARISVMS_SERVER_CERT_THUMBPRINT: {Quote(thumbprint)}");
        sb.AppendLine($"      LARISVMS_CLIENT_PORT: '{clientPort}'");
        sb.AppendLine("    volumes:");
        sb.AppendLine("      - larisvms-proxy-data:/var/lib/larisvms");
        sb.AppendLine("volumes:");
        sb.Append("  larisvms-proxy-data:");
        return sb.ToString();
    }

    /// <summary>A YAML single-quoted scalar: only a quote itself needs escaping, by doubling it.</summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    [GeneratedRegex(@"^\d+\.\d+\.\d+")]
    private static partial Regex ReleaseVersion();
}
