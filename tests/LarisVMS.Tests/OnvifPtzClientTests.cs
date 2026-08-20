using LarisVMS.Onvif.Clients;

namespace LarisVMS.Tests;

public class OnvifPtzClientTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1")]
    [InlineData(-1, "-1")]
    [InlineData(0.5, "0.5")]
    [InlineData(-0.333333, "-0.333")]
    public void FormatsAxisValuesForXmlRegardlessOfServerLocale(double value, string expected)
    {
        Assert.Equal(expected, OnvifPtzClient.FormatAxis(value));
    }
}
