using System.Net;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class SignInThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly IPAddress Here = IPAddress.Parse("203.0.113.5");
    private static readonly IPAddress There = IPAddress.Parse("198.51.100.7");

    [Fact]
    public void ADomainPrefixDoesntGiveTheSameAccountMoreTries()
    {
        var throttle = new SignInThrottle(new Clock());
        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure($"dom{i}\\bob", Here);
        Assert.NotNull(throttle.BlockedFor("bob", Here));
        Assert.NotNull(throttle.BlockedFor("CORP\\Bob", Here));
    }

    [Fact]
    public void AnIPv6NetworkCountsAsOneClient()
    {
        var throttle = new SignInThrottle(new Clock());
        for (var i = 1; i <= SignInThrottle.MaxFailures; i++)
            throttle.RecordFailure("admin@example.com", IPAddress.Parse($"2001:db8:1:2::{i:x}"));
        Assert.NotNull(throttle.BlockedFor("admin@example.com", IPAddress.Parse("2001:db8:1:2:aaaa::1")));
        Assert.Null(throttle.BlockedFor("admin@example.com", IPAddress.Parse("2001:db8:1:3::1")));
        Assert.Equal("192.0.2.4", SignInThrottle.AddressKey(IPAddress.Parse("::ffff:192.0.2.4")));
    }

    [Fact]
    public void OneAddressIsTurnedAwayAfterFiveWrongTriesWithoutLockingOutAnyoneElse()
    {
        var clock = new Clock();
        var throttle = new SignInThrottle(clock);
        for (var i = 0; i < SignInThrottle.MaxFailures - 1; i++) throttle.RecordFailure("admin@example.com", Here);
        Assert.Null(throttle.BlockedFor("admin@example.com", Here));

        throttle.RecordFailure("ADMIN@example.com ", Here); // same name, however it's typed
        Assert.NotNull(throttle.BlockedFor("admin@example.com", Here));
        Assert.Null(throttle.BlockedFor("admin@example.com", There));  // the real person elsewhere
        Assert.Null(throttle.BlockedFor("other@example.com", Here));

        clock.Now += SignInThrottle.Window + TimeSpan.FromSeconds(1);
        Assert.Null(throttle.BlockedFor("admin@example.com", Here));
    }

    [Fact]
    public void TriesSpreadOutBeyondTheWindowDontAddUp()
    {
        var clock = new Clock();
        var throttle = new SignInThrottle(clock);
        for (var i = 0; i < 10; i++)
        {
            throttle.RecordFailure("jane", Here);
            if (i % 4 == 3) clock.Now += SignInThrottle.Window + TimeSpan.FromMinutes(1);
        }
        Assert.Null(throttle.BlockedFor("jane", Here));
    }

    [Fact]
    public void ASuccessfulSignInClearsTheCount()
    {
        var throttle = new SignInThrottle(new Clock());
        for (var i = 0; i < SignInThrottle.MaxFailures - 1; i++) throttle.RecordFailure("jane", Here);
        throttle.RecordSuccess("jane", Here);
        throttle.RecordFailure("jane", Here);
        Assert.Null(throttle.BlockedFor("jane", Here));
    }

    [Fact]
    public void AnIpv4AddressCountsTheSameWhenItArrivesAsIpv6()
    {
        var throttle = new SignInThrottle(new Clock());
        for (var i = 0; i < SignInThrottle.MaxFailures; i++) throttle.RecordFailure("jane", Here.MapToIPv6());
        Assert.NotNull(throttle.BlockedFor("jane", Here));
    }
}
