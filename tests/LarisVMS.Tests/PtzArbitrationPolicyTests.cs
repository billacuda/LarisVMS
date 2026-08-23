using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class PtzArbitrationPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GrantedWhenNobodyCurrentlyHoldsTheCamera()
    {
        var result = PtzArbitrationPolicy.CanAcquire(null, "user-a", requesterPriority: 1000, Now);
        Assert.True(result.Granted);
        Assert.Null(result.RetryAfterSeconds);
    }

    [Fact]
    public void GrantedWhenTheRequesterAlreadyHoldsItThemselves()
    {
        var hold = new PtzHoldSnapshot("user-a", PriorityLevel: 3000, LockoutSeconds: 900, LastCommandUtc: Now);
        var result = PtzArbitrationPolicy.CanAcquire(hold, "user-a", requesterPriority: 1000, Now);
        Assert.True(result.Granted);
    }

    [Fact]
    public void GrantedWhenTheRequesterHasStrictlyHigherPriority()
    {
        var hold = new PtzHoldSnapshot("user-a", PriorityLevel: 1000, LockoutSeconds: 900, LastCommandUtc: Now);
        var result = PtzArbitrationPolicy.CanAcquire(hold, "user-b", requesterPriority: 1500, Now);
        Assert.True(result.Granted);
    }

    [Fact]
    public void DeniedWhenTheRequesterHasEqualPriority()
    {
        var hold = new PtzHoldSnapshot("user-a", PriorityLevel: 1500, LockoutSeconds: 900, LastCommandUtc: Now);
        var result = PtzArbitrationPolicy.CanAcquire(hold, "user-b", requesterPriority: 1500, Now);
        Assert.False(result.Granted);
    }

    [Fact]
    public void DeniedWhenTheRequesterHasLowerPriorityAndTheHoldersLockoutHasNotElapsed()
    {
        var hold = new PtzHoldSnapshot("user-a", PriorityLevel: 2000, LockoutSeconds: 900,
            LastCommandUtc: Now.AddSeconds(-100));
        var result = PtzArbitrationPolicy.CanAcquire(hold, "user-b", requesterPriority: 1000, Now);
        Assert.False(result.Granted);
        Assert.Equal(800, result.RetryAfterSeconds);
    }

    [Fact]
    public void GrantedWhenTheHoldersOwnLockoutWindowHasFullyElapsed()
    {
        var hold = new PtzHoldSnapshot("user-a", PriorityLevel: 2000, LockoutSeconds: 900,
            LastCommandUtc: Now.AddSeconds(-901));
        var result = PtzArbitrationPolicy.CanAcquire(hold, "user-b", requesterPriority: 1000, Now);
        Assert.True(result.Granted);
    }

    [Fact]
    public void UsesTheHoldersOwnLockoutWindowNeverTheRequesters()
    {
        // Holder's lockout is long (900s) and only 100s have elapsed — a requester with a much
        // shorter lockout of their own must not shortcut the holder's still-active window.
        var hold = new PtzHoldSnapshot("user-a", PriorityLevel: 2000, LockoutSeconds: 900,
            LastCommandUtc: Now.AddSeconds(-100));
        var result = PtzArbitrationPolicy.CanAcquire(hold, "user-b", requesterPriority: 1000, Now);
        Assert.False(result.Granted);
        Assert.Equal(800, result.RetryAfterSeconds);
    }
}
