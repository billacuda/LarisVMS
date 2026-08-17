using LarisVMS.Core;

namespace LarisVMS.Tests;

/// <summary>Covers the plugin registry and auto-detection. The make/model strings here are the real
/// values this deployment's cameras report over ONVIF, not invented ones — detection running on
/// live data is the whole point, so it's worth pinning against what the hardware actually says.</summary>
public class CameraIntegrationRegistryTests
{
    [Theory]
    [InlineData("Amcrest", "IP5M-B1276EW-AI")]
    [InlineData("Amcrest", "IP8M-DLB2998EW-AI")]
    [InlineData("Dahua", "IPC-HDW3849H-AS-PV")]
    [InlineData("amcrest", "ip5m-b1276ew-ai")]      // case-insensitive
    [InlineData("Lorex", "N4K2-88")]
    public void DetectsTheDahuaFamily(string manufacturer, string model)
    {
        var provider = CameraIntegrations.Detect(manufacturer, model);

        Assert.NotNull(provider);
        Assert.Equal(DahuaCgiIntegrationProvider.ProviderKey, provider!.Key);
    }

    [Theory]
    [InlineData("Hikvision", "DS-2CD2143G0-I")]
    [InlineData("Axis", "P3245-LVE")]
    [InlineData("Reolink", "RLC-810A")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void LeavesEverythingElseOnPlainOnvif(string? manufacturer, string? model)
    {
        // No integration is the correct answer for most cameras — ONVIF alone is enough, and
        // guessing wrong would start a pointless long-poll against an endpoint that isn't there.
        Assert.Null(CameraIntegrations.Detect(manufacturer, model));
    }

    [Fact]
    public void ResolvesAProviderByItsPersistedKey()
    {
        var provider = CameraIntegrations.ByKey(DahuaCgiIntegrationProvider.ProviderKey);

        Assert.NotNull(provider);
        Assert.Equal("dahua-cgi", provider!.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a-provider-that-was-removed")]
    public void AnUnknownKeyResolvesToNothingRatherThanThrowing(string? key)
    {
        // A camera row can outlive the provider that set it (a downgrade, or a provider dropped in a
        // later version). That must degrade to "no integration" rather than break config generation
        // for every camera on the node.
        Assert.Null(CameraIntegrations.ByKey(key));
    }

    [Fact]
    public void EveryRegisteredProviderIsWellFormed()
    {
        Assert.NotEmpty(CameraIntegrations.All);

        var keys = CameraIntegrations.All.Select(p => p.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.All(CameraIntegrations.All, provider =>
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.Key));
            Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(provider.Summary));
            // A provider must resolve by its own key, or a camera could never be matched back to it.
            Assert.Same(provider, CameraIntegrations.ByKey(provider.Key));
        });
    }
}
