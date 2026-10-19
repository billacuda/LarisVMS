namespace LarisVMS.Web.Services;

/// <summary>Which HTTP versions the web server started with. Kestrel's protocols are fixed at startup,
/// so Settings → Security compares the saved HTTP/3 switch against <see cref="Http3Active"/> to tell
/// the admin when a restart is needed.</summary>
/// <param name="Http3Enabled">The <see cref="Http3SettingKey"/> value read at startup.</param>
/// <param name="Http3Supported">Whether the OS has QUIC (Windows 11 / Server 2022 or later).</param>
public sealed record WebServerProtocols(bool Http3Enabled, bool Http3Supported)
{
    public const string Http3SettingKey = "WebServer.Http3Enabled";

    public bool Http3Active => Http3Enabled && Http3Supported;
}
