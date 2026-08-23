using LarisVMS.Infrastructure.Security;

namespace LarisVMS.Tests;

public class SecretHashTests
{
    [Fact]
    public void MatchesReturnsTrueForTheSecretThatProducedTheHash()
    {
        var secret = "correct-horse-battery-staple";
        var hash = SecretHash.Hash(secret);

        Assert.True(SecretHash.Matches(secret, hash));
    }

    [Fact]
    public void MatchesReturnsFalseForAnyOtherSecret()
    {
        var hash = SecretHash.Hash("the-real-secret");

        Assert.False(SecretHash.Matches("a-guess", hash));
    }

    [Fact]
    public void HashIsDeterministicAndNeverEqualsTheRawSecret()
    {
        var secret = "some-generated-value";

        Assert.Equal(SecretHash.Hash(secret), SecretHash.Hash(secret));
        Assert.NotEqual(secret, SecretHash.Hash(secret));
    }

    [Fact]
    public void FixedTimeEqualsComparesPlaintextDirectlyNotAsAHash()
    {
        Assert.True(SecretHash.FixedTimeEquals("same-value", "same-value"));
        Assert.False(SecretHash.FixedTimeEquals("value-a", "value-b"));
    }
}
