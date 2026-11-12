namespace LarisVMS.Core.Security;

/// <summary>
/// Where a node or proxy downloads an offered update from. The download carries its credentials, so
/// it always goes to the server it's registered with: the offered address's path and query on the
/// configured server's own scheme, host, and port. An offer naming another host (a tampered offer, or
/// a server that built the address from a spoofed Host header) can't collect them.
/// </summary>
public static class UpdateDownload
{
    public static Uri Resolve(string serverUrl, string downloadUrl)
    {
        var server = new Uri(serverUrl.EndsWith('/') ? serverUrl : serverUrl + "/", UriKind.Absolute);
        var offered = Uri.TryCreate(downloadUrl, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute.PathAndQuery
            : downloadUrl;
        if (!offered.StartsWith('/')) offered = "/" + offered;
        return new Uri(new Uri(server.GetLeftPart(UriPartial.Authority)), offered);
    }

    /// <summary>An offered version that can't be read is refused rather than let through the
    /// downgrade check; a running version that can't be read (a development build) allows any.</summary>
    public static bool IsNewer(string offeredVersion, string runningVersion) =>
        Version.TryParse(offeredVersion, out var offered)
        && (!Version.TryParse(runningVersion, out var running) || offered > running);
}
