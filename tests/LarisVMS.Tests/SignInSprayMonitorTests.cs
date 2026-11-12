using System.Net;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Password guessing spread over many networks, which the per-address limits never trip, raises a
/// notice: for one account guessed from several networks, and for many failures across the server.
/// </summary>
public class SignInSprayMonitorTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static IPAddress Net(int i) => IPAddress.Parse($"203.0.{i}.7");

    [Fact]
    public void OneAccountGuessedFromSeveralNetworksIsNoticedOnce()
    {
        var monitor = new SignInSprayMonitor(new Clock());
        SignInSprayMonitor.Notice? notice = null;
        for (var i = 0; i < SignInSprayMonitor.AccountFailures; i++)
            notice ??= monitor.RecordFailure(i % 2 == 0 ? "admin" : "CORP\\admin", Net(i % 4));

        var account = Assert.IsType<SignInSprayMonitor.AccountNotice>(notice);
        Assert.Equal(SignInSprayMonitor.AccountFailures, account.Failures);
        Assert.Equal(4, account.Networks);
        Assert.Null(monitor.RecordFailure("admin", Net(9))); // not again within the hour
    }

    [Fact]
    public void ManyFailuresFromOneNetworkAreLeftToThePerAddressLimits()
    {
        var monitor = new SignInSprayMonitor(new Clock());
        for (var i = 0; i < 60; i++)
            Assert.Null(monitor.RecordFailure($"user{i}", IPAddress.Parse("198.51.100.4")));
    }

    [Fact]
    public void OnePasswordTriedAcrossManyAccountsIsNoticed()
    {
        var monitor = new SignInSprayMonitor(new Clock());
        SignInSprayMonitor.Notice? notice = null;
        for (var i = 0; i < SignInSprayMonitor.ServerFailures; i++)
            notice ??= monitor.RecordFailure($"user{i}", Net(i % 12));

        var server = Assert.IsType<SignInSprayMonitor.ServerNotice>(notice);
        Assert.Equal(12, server.Networks);
        Assert.Equal(SignInSprayMonitor.ServerFailures, server.Accounts);
    }

    [Fact]
    public void FailuresOlderThanTheWindowDontCount()
    {
        var clock = new Clock();
        var monitor = new SignInSprayMonitor(clock);
        for (var i = 0; i < SignInSprayMonitor.AccountFailures - 1; i++) monitor.RecordFailure("admin", Net(i));
        clock.Now += SignInSprayMonitor.Window + TimeSpan.FromSeconds(1);
        Assert.Null(monitor.RecordFailure("admin", Net(1)));
    }
}
