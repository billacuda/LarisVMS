using LarisVMS.Web.Helpers;

namespace LarisVMS.Tests;

/// <summary>
/// What the Docker images rely on: the direct-streaming endpoint configured from LARISVMS_CLIENT_*
/// environment variables (a container has no setup command to write the json), the compose snippet
/// shown on the setup wizard and the Media proxies page, and the "Docker" platform label.
/// </summary>
public class DockerContainerTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Key, v => v.Value);
        return key => map.GetValueOrDefault(key);
    }

    [Fact]
    public void ProxyEndpointWithoutVariablesIsTheFileAsItIs()
    {
        var local = new LarisVMS.Proxy.ProxyEndpointLocalConfig(Port: 5000, Host: "proxy.example.com");
        Assert.Same(local, LarisVMS.Proxy.ProxyEndpointConfig.WithEnvironment(local, Env()));
        Assert.Null(LarisVMS.Proxy.ProxyEndpointConfig.WithEnvironment(null, Env()));
    }

    [Fact]
    public void ProxyEndpointVariablesWinOverTheFile()
    {
        var local = new LarisVMS.Proxy.ProxyEndpointLocalConfig(Port: 5000, PfxPath: "/etc/larisvms/old.pfx", Host: "old.example.com");
        var merged = LarisVMS.Proxy.ProxyEndpointConfig.WithEnvironment(local, Env(
            ("LARISVMS_CLIENT_PORT", "4443"), ("LARISVMS_CLIENT_PFX_PATH", "/certs/proxy.pfx"), ("LARISVMS_CLIENT_ALLOW_INSECURE", "true")))!;

        Assert.Equal(4443, merged.Port);
        Assert.Equal("/certs/proxy.pfx", merged.PfxPath);
        Assert.True(merged.AllowInsecure);
        Assert.Equal("old.example.com", merged.Host);   // not set: the file's value stays
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void AnInvalidPortVariableIsIgnored(string port)
    {
        var merged = LarisVMS.Proxy.ProxyEndpointConfig.WithEnvironment(null, Env(("LARISVMS_CLIENT_PORT", port)));
        Assert.Null(merged);
    }

    [Fact]
    public void ANodeClientPortVariableTurnsTheEndpointOn()
    {
        var merged = LarisVMS.Node.ClientEndpointConfig.WithEnvironment(null, Env(("LARISVMS_CLIENT_PORT", "8555"), ("LARISVMS_CLIENT_ENDPOINT_HOST", "nvr1.example.com")))!;

        Assert.True(merged.Enabled);
        Assert.Equal(8555, merged.Port);
        Assert.Equal("nvr1.example.com", merged.Host);
    }

    [Fact]
    public void ANodeCertificateVariableAloneLeavesTheEndpointAsItWas()
    {
        var merged = LarisVMS.Node.ClientEndpointConfig.WithEnvironment(null, Env(("LARISVMS_CLIENT_PFX_PATH", "/certs/node.pfx")))!;

        Assert.False(merged.Enabled);
        Assert.Equal("/certs/node.pfx", merged.PfxPath);
    }

    [Theory]
    [InlineData("0.223.0", "0.223.0")]
    [InlineData("0.223.0+4f2a9c1", "0.223.0")]
    [InlineData("0.223.0.245", "0.223.0")]
    [InlineData(null, "latest")]
    [InlineData("dev", "latest")]
    public void TheImageTagIsTheServersReleaseVersion(string? running, string expected) =>
        Assert.Equal(expected, DockerComposeSnippet.ImageTag(running));

    [Fact]
    public void TheNodeSnippetCarriesTheServerKeyAndThumbprintQuoted()
    {
        var yaml = DockerComposeSnippet.Node("https://vms.example.com:8444", "k3y:#with'quote", "AB12", "0.223.0+abc");

        Assert.Contains("image: ghcr.io/billacuda/larisvms-node:0.223.0", yaml);
        Assert.Contains("LARISVMS_SERVER_URL: 'https://vms.example.com:8444'", yaml);
        Assert.Contains("LARISVMS_REGISTRATION_KEY: 'k3y:#with''quote'", yaml);   // YAML doubles a quote inside single quotes
        Assert.Contains("LARISVMS_SERVER_CERT_THUMBPRINT: 'AB12'", yaml);
        Assert.Contains("network_mode: host", yaml);
        Assert.Contains("- larisvms-node-data:/var/lib/larisvms", yaml);
    }

    [Fact]
    public void TheSnippetsLeaveOutTheThumbprintWhenThereIsNone()
    {
        Assert.DoesNotContain("THUMBPRINT", DockerComposeSnippet.Node("https://a:8444", "key", null, "0.223.0"));
        Assert.DoesNotContain("THUMBPRINT", DockerComposeSnippet.Proxy("https://a:8444", "key", null, "0.223.0"));
    }

    [Fact]
    public void TheProxySnippetSetsItsClientPort()
    {
        var yaml = DockerComposeSnippet.Proxy("https://a:8444", "key", null, "0.223.0", clientPort: 4443);

        Assert.Contains("image: ghcr.io/billacuda/larisvms-proxy:0.223.0", yaml);
        Assert.Contains("LARISVMS_CLIENT_PORT: '4443'", yaml);
    }

    [Fact]
    public void TheSnippetsIndentWithSpacesOnly() =>
        Assert.DoesNotContain('\t', DockerComposeSnippet.Node("https://a:8444", "key", "AB", "0.223.0") + DockerComposeSnippet.Proxy("https://a:8444", "key", "AB", "0.223.0"));

    [Fact]
    public void AContainerShowsDockerBesideItsPlatform()
    {
        Assert.Equal("Linux x64 · Docker", PlatformLabels.Display("linux-x64", inContainer: true));
        Assert.Equal("Linux x64", PlatformLabels.Display("linux-x64", inContainer: false));
    }
}
