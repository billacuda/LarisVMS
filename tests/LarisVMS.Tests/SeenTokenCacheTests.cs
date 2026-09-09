using LarisVMS.Node;

namespace LarisVMS.Tests;

public class SeenTokenCacheTests
{
    [Fact]
    public void AllowsAFirstUseAndRejectsASecond()
    {
        var cache = new SeenTokenCache();
        var jti = "0011223344556677";

        Assert.True(cache.TryConsume(jti));
        Assert.False(cache.TryConsume(jti));
        Assert.False(cache.TryConsume(jti));
    }

    [Fact]
    public void DifferentJtisAreIndependent()
    {
        var cache = new SeenTokenCache();

        Assert.True(cache.TryConsume("aaaa"));
        Assert.True(cache.TryConsume("bbbb"));
        Assert.False(cache.TryConsume("aaaa"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ANullOrEmptyJtiIsAlwaysAllowed(string? jti)
    {
        var cache = new SeenTokenCache();

        // A v1 token (older Web tier) or the multi-use live-view token carries no jti — every use
        // must pass, and nothing is stored to later collide with.
        Assert.True(cache.TryConsume(jti));
        Assert.True(cache.TryConsume(jti));
    }
}
