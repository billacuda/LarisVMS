using LarisVMS.Installer;

namespace LarisVMS.Tests;

public sealed class InstallerUtilTests
{
    [Theory]
    [InlineData("8554", "8554")]
    [InlineData("", "\"\"")]
    [InlineData(@"D:\Rec Ordings", @"""D:\Rec Ordings""")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void QuoteArg_MatchesFormatServiceArg(string value, string expected) =>
        Assert.Equal(expected, Util.QuoteArg(value));

    [Fact]
    public void SplitCommandLine_RoundTripsQuotedArgs()
    {
        var args = new[] { @"C:\Program Files\LarisVMS\Node\LarisVMS.Node.exe", "--storage-root", @"D:\Rec Ordings", "--x", "a\"b", "" };
        var line = string.Join(" ", args.Select(Util.QuoteArg));
        Assert.Equal(args, Util.SplitCommandLine(line));
    }

    [Fact]
    public void JsonString_RoundTripsThroughJsonReadString()
    {
        var value = @"C:\certs\vms ""prod"".pfx" + "\n\t\u0001";
        var json = "{ \"pfxPath\": " + Util.JsonString(value) + ", \"port\": 4443 }";
        Assert.Equal(value, System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("pfxPath").GetString());
        Assert.Equal(value, Util.JsonReadString(json, "PfxPath"));
        Assert.Null(Util.JsonReadString(json, "missing"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("LocalSystem", false)]
    [InlineData(@"NT AUTHORITY\NetworkService", false)]
    [InlineData(@"CORP\svc-vms$", false)]
    [InlineData(@"CORP\svc-vms", true)]
    public void NeedsPassword(string? account, bool expected) =>
        Assert.Equal(expected, Util.NeedsPassword(account));

    [Theory]
    [InlineData("1", true)]
    [InlineData("65535", true)]
    [InlineData("0", false)]
    [InlineData("65536", false)]
    [InlineData("abc", false)]
    [InlineData(null, false)]
    public void IsPort(string? value, bool expected) => Assert.Equal(expected, Util.IsPort(value));

    [Theory]
    [InlineData(@"""\\files1\certs$\x.pfx""", @"\\files1\certs$\x.pfx")]
    [InlineData(@"  ""C:\My Certs\vms.pfx""  ", @"C:\My Certs\vms.pfx")]
    [InlineData(@"C:\certs\vms.pfx", @"C:\certs\vms.pfx")]
    [InlineData(@"""C:\certs\vms.pfx", @"""C:\certs\vms.pfx")]
    [InlineData(@"""", @"""")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void CleanPath_StripsSurroundingQuotes(string? value, string expected) =>
        Assert.Equal(expected, Util.CleanPath(value));

    [Fact]
    public void ReadEndpointConfig_ReadsANodesClientEndpoint()
    {
        var json = """
            {
              "enabled": true,
              "port": 4200,
              "allowInsecure": false,
              "pfxPath": "\\\\files1\\certs$\\wildcard.example.pfx",
              "pfxPassword": "secret"
            }
            """;
        var config = Util.ReadEndpointConfig(json, requireEnabled: true);

        Assert.NotNull(config);
        Assert.Equal("4200", config!.Port);
        Assert.Equal(@"\\files1\certs$\wildcard.example.pfx", config.PfxPath);
        Assert.Null(config.Host);
        Assert.False(config.AllowInsecure);
    }

    [Fact]
    public void ReadEndpointConfig_ReadsAProxyEndpointWithoutAnEnabledFlag()
    {
        var json = """{ "port": 5443, "allowInsecure": true, "host": "proxy.example.com" }""";
        var config = Util.ReadEndpointConfig(json, requireEnabled: false);

        Assert.Equal("5443", config!.Port);
        Assert.Equal("proxy.example.com", config.Host);
        Assert.True(config.AllowInsecure);
    }

    [Theory]
    [InlineData("""{ "enabled": false, "port": 4200 }""")]
    [InlineData("""{ "port": 4200 }""")]
    [InlineData("""{ "enabled": true, "port": 0 }""")]
    [InlineData("""{ "enabled": true }""")]
    public void ReadEndpointConfig_SkipsADisabledOrIncompleteNodeEndpoint(string json) =>
        Assert.Null(Util.ReadEndpointConfig(json, requireEnabled: true));

    [Fact]
    public void CertBlock_IgnoresOtherPathAndPasswordKeys()
    {
        var json = """
            {
              "Logging": { "LogLevel": { "Default": "Information" }, "Path": "D:\\logs" },
              "Smtp": { "Password": "not-this" },
              "Kestrel": { "Certificates": { "Default": { "Path": "\\\\files1\\certs$\\x.pfx", "Password": "p^w-d" } }, "HttpsPort": 8444 }
            }
            """;
        var block = Util.CertBlock(json);
        Assert.NotNull(block);
        Assert.Equal(@"\\files1\certs$\x.pfx", Util.JsonReadString(block!.Value, "Path"));
        Assert.Equal("p^w-d", Util.JsonReadString(block.Value, "Password"));
        Assert.Null(Util.CertBlock("{ \"Kestrel\": { \"HttpsPort\": 8444 } }"));
    }

    [Fact]
    public void CheckPfx_ReportsWrongPasswordAndMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "laris-checkpfx-" + Guid.NewGuid().ToString("N") + ".pfx");
        using (var rsa = System.Security.Cryptography.RSA.Create(2048))
        {
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(path, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, "right-^-pass"));
        }
        try
        {
            Assert.Null(Util.CheckPfx(path, "right-^-pass", "CERTPATH", "CERTPASSWORD"));
            Assert.Contains("CERTPASSWORD", Util.CheckPfx(path, "wrong", "CERTPATH", "CERTPASSWORD"));
            Assert.Contains("CERTPATH", Util.CheckPfx(path + ".missing", "right-^-pass", "CERTPATH", "CERTPASSWORD"));
        }
        finally { File.Delete(path); }
    }
}
