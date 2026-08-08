namespace Rcordr.Tests;

public class PlaceholderTests
{
    [Fact]
    public void AppVersion_ToVersionString_FormatsCorrectly()
    {
        var version = new Rcordr.Core.Entities.AppVersion { Major = 0, Minor = 1, Patch = 0 };
        Assert.Equal("0.1.0", version.ToVersionString());
    }
}
