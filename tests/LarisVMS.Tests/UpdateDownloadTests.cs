using LarisVMS.Core.Security;

namespace LarisVMS.Tests;

/// <summary>
/// A node or proxy downloads an update (with its credentials) only from the server it's registered
/// with, and an offer whose version can't be read isn't let past the downgrade check.
/// </summary>
public class UpdateDownloadTests
{
    [Theory]
    [InlineData("https://vms.example.com:8444", "https://vms.example.com:8444/api/nodes/update/download?v=1", "https://vms.example.com:8444/api/nodes/update/download?v=1")]
    [InlineData("https://vms.example.com:8444/", "https://evil.example/api/nodes/update/download", "https://vms.example.com:8444/api/nodes/update/download")]
    [InlineData("https://10.0.0.2:8444", "/api/proxies/update/download", "https://10.0.0.2:8444/api/proxies/update/download")]
    [InlineData("https://vms.example.com:8444", "api/x", "https://vms.example.com:8444/api/x")]
    [InlineData("https://vms.example.com:8444", "file:///C:/Windows/evil.exe", "https://vms.example.com:8444/file:///C:/Windows/evil.exe")]
    public void TheDownloadAlwaysGoesToTheRegisteredServer(string server, string offered, string expected) =>
        Assert.Equal(expected, UpdateDownload.Resolve(server, offered).ToString());

    [Theory]
    [InlineData("0.221.0", "0.220.0", true)]
    [InlineData("0.220.0", "0.220.0", false)]
    [InlineData("0.219.0", "0.220.0", false)]
    [InlineData("not-a-version", "0.220.0", false)]
    [InlineData("0.221.0", "dev", true)]
    public void OnlyANewerReadableVersionIsInstalled(string offered, string running, bool newer) =>
        Assert.Equal(newer, UpdateDownload.IsNewer(offered, running));
}
