using NidusVMS.Core.Security;

namespace NidusVMS.Tests;

public class MediaTokenTests
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";

    [Fact]
    public void RoundTripsForTheCameraItWasIssuedFor()
    {
        var cameraId = Guid.NewGuid();
        var token = MediaToken.Issue(cameraId, Key, TimeSpan.FromSeconds(60));

        Assert.True(MediaToken.TryValidate(token, cameraId, Key, out _));
    }

    [Fact]
    public void RejectsATokenIssuedForADifferentCamera()
    {
        var token = MediaToken.Issue(Guid.NewGuid(), Key, TimeSpan.FromSeconds(60));

        Assert.False(MediaToken.TryValidate(token, Guid.NewGuid(), Key, out var error));
        Assert.Equal("camera mismatch", error);
    }

    [Fact]
    public void RejectsAnExpiredToken()
    {
        var cameraId = Guid.NewGuid();
        var token = MediaToken.Issue(cameraId, Key, TimeSpan.FromSeconds(-1));

        Assert.False(MediaToken.TryValidate(token, cameraId, Key, out var error));
        Assert.Equal("expired", error);
    }

    [Fact]
    public void RejectsATokenSignedWithADifferentKey()
    {
        var cameraId = Guid.NewGuid();
        var token = MediaToken.Issue(cameraId, Key, TimeSpan.FromSeconds(60));
        var wrongKey = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba98765432";

        Assert.False(MediaToken.TryValidate(token, cameraId, wrongKey, out var error));
        Assert.Equal("signature mismatch", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    [InlineData("payload-with-no-dot-separator")]
    [InlineData("cameraid:123.not-hex-!!")]
    public void RejectsMalformedTokens(string token)
    {
        Assert.False(MediaToken.TryValidate(token, Guid.NewGuid(), Key, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RejectsANullToken()
    {
        Assert.False(MediaToken.TryValidate(null, Guid.NewGuid(), Key, out var error));
        Assert.Equal("missing token", error);
    }
}
