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
}
