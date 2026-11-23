using System.Text.Json;
using LarisVMS.Vision.Inference;

namespace LarisVMS.Tests;

/// <summary>OpenVINO's compiled-model cache is passed as JSON in the provider's load_config; a
/// Windows path's backslashes must survive the round trip or OpenVINO caches nowhere.</summary>
public sealed class OpenVinoCacheTests
{
    [Theory]
    [InlineData("GPU", @"C:\ProgramData\LarisVMS\openvino-cache")]
    [InlineData("NPU", "/var/lib/larisvms/openvino-cache")]
    public void LoadConfigCarriesTheCacheDirForTheDevice(string device, string cachePath)
    {
        var json = OrtSessionFactory.OpenVinoLoadConfig(device, cachePath);

        var parsed = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)!;
        Assert.Equal(cachePath, parsed[device]["CACHE_DIR"]);
    }

    [Fact]
    public void DefaultsToTheDataFolder()
    {
        Assert.Equal(Path.Combine(LarisVMS.Core.LarisPaths.DataRoot, "openvino-cache"), OrtSessionFactory.ResolveOpenVinoCachePath(null));
        Assert.Equal("/x", OrtSessionFactory.ResolveOpenVinoCachePath("/x"));
    }
}
