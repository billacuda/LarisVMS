using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// The per-role session lifetime decision — extracted from Program.cs's cookie OnValidatePrincipal
/// handler specifically so this is testable without standing up a real authentication pipeline.
/// </summary>
public class SessionLifetimePolicyTests
{
    [Fact]
    public void DefaultsCorrectlyReflectTheOriginalAsk()
    {
        // Sessions were expiring too fast at a fixed 60-minute cookie timeout; the ask was a
        // per-role setting defaulting to 24 hours, with 0 meaning never.
        Assert.Equal(24, SessionLifetimePolicy.DefaultHours);
    }

    [Fact]
    public void ASingleRoleWithAPositiveValueUsesItDirectly()
    {
        var window = SessionLifetimePolicy.EffectiveWindow([12]);
        Assert.Equal(TimeSpan.FromHours(12), window);
    }

    [Fact]
    public void ASingleRoleSetToZeroNeverExpires()
    {
        Assert.Null(SessionLifetimePolicy.EffectiveWindow([0]));
    }

    [Fact]
    public void NoRolesAtAllNeverExpires()
    {
        Assert.Null(SessionLifetimePolicy.EffectiveWindow([]));
    }

    [Fact]
    public void TheShortestPositiveWindowAmongSeveralRolesWins()
    {
        var window = SessionLifetimePolicy.EffectiveWindow([24, 8, 48]);
        Assert.Equal(TimeSpan.FromHours(8), window);
    }

    [Fact]
    public void AZeroInOneRoleDoesNotOverrideAStricterRolesOwnLimit()
    {
        // The case that matters most: a user holding both an "unlimited" role and a strict one must
        // still be bound by the strict one — "never" on one role is not an escape hatch.
        var window = SessionLifetimePolicy.EffectiveWindow([0, 4]);
        Assert.Equal(TimeSpan.FromHours(4), window);
    }

    [Fact]
    public void OnlyEveryRoleBeingZeroMeansTrulyUnlimited()
    {
        Assert.Null(SessionLifetimePolicy.EffectiveWindow([0, 0, 0]));
    }

    [Fact]
    public void HasNotExpiredWithinTheWindow()
    {
        var issued = DateTimeOffset.UtcNow;
        Assert.False(SessionLifetimePolicy.HasExpired(issued, issued.AddHours(23), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void HasExpiredPastTheWindow()
    {
        var issued = DateTimeOffset.UtcNow;
        Assert.True(SessionLifetimePolicy.HasExpired(issued, issued.AddHours(25), TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ANullWindowNeverExpiresRegardlessOfAge()
    {
        var issued = DateTimeOffset.UtcNow;
        Assert.False(SessionLifetimePolicy.HasExpired(issued, issued.AddYears(10), null));
    }

    [Fact]
    public void SettingKeysAreDistinctPerRole()
    {
        Assert.NotEqual(SessionLifetimePolicy.SettingKey("role-a"), SessionLifetimePolicy.SettingKey("role-b"));
        Assert.StartsWith(SessionLifetimePolicy.SettingKeyPrefix, SessionLifetimePolicy.SettingKey("role-a"));
    }
}
