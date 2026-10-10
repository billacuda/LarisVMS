using LarisVMS.Core.Entities;
using LarisVMS.Infrastructure.ActiveDirectory;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

public class ActiveDirectoryHelpersTests
{
    // ── Sign-in name routing ─────────────────────────────────────────────────

    [Theory]
    [InlineData("admin@example.com", true)]
    [InlineData("jdoe", false)]
    [InlineData(@"CORP\jdoe", false)]
    public void AnythingWithAnAtSignIsALocalAccount(string input, bool isLocal)
    {
        Assert.Equal(isLocal, AdUsername.IsLocal(input));
    }

    [Theory]
    [InlineData("jdoe", "jdoe")]
    [InlineData(@"CORP\jdoe", "jdoe")]
    [InlineData("  jdoe  ", "jdoe")]
    [InlineData(@"corp.example.com\jdoe", "jdoe")]
    public void DomainPrefixIsDropped(string input, string expected)
    {
        Assert.Equal(expected, AdUsername.Normalize(input));
    }

    [Theory]
    [InlineData(@"CORP\svc-vms", "CORP", "svc-vms")]
    [InlineData("svc-vms@corp.example.com", "", "svc-vms@corp.example.com")]
    [InlineData("svc-vms", "corp", "svc-vms")]
    public void ServiceAccountIsSplitIntoDomainAndUser(string input, string domain, string user)
    {
        Assert.Equal((domain, user), AdUsername.SplitServiceAccount(input, "corp.example.com"));
    }

    // ── Domain ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("corp.example.com", true)]
    [InlineData("example.local", true)]
    [InlineData("corp", false)]
    [InlineData("DC=corp,DC=example,DC=com", false)]
    [InlineData("corp..example.com", false)]
    [InlineData("-corp.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DomainMustBeADnsName(string? domain, bool valid)
    {
        Assert.Equal(valid, AdDomain.IsValid(domain));
    }

    [Fact]
    public void BaseDnIsDerivedFromTheDomainName()
    {
        Assert.Equal("DC=corp,DC=example,DC=com", AdDomain.ToBaseDn("corp.example.com"));
    }

    // ── LDAP filter escaping ─────────────────────────────────────────────────

    [Fact]
    public void FilterSpecialCharactersAreEscaped()
    {
        Assert.Equal(@"a\2ab\28c\29d\5ce\00", LdapFilter.Escape("a*b(c)d\\e\0"));
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        Assert.Equal("VMS Operators-01", LdapFilter.Escape("VMS Operators-01"));
    }

    // ── SIDs ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BinarySidConvertsToStringForm()
    {
        // S-1-5-21-1004336348-1177238915-682003330-512 (a Domain Admins SID).
        byte[] sid =
        [
            1, 5, 0, 0, 0, 0, 0, 5,
            21, 0, 0, 0,
            0xDC, 0xF4, 0xDC, 0x3B,
            0x83, 0x3D, 0x2B, 0x46,
            0x82, 0x8B, 0xA6, 0x28,
            0x00, 0x02, 0x00, 0x00
        ];
        Assert.Equal("S-1-5-21-1004336348-1177238915-682003330-512", AdSid.ToSddl(sid));
    }

    [Fact]
    public void TruncatedSidIsRejected()
    {
        Assert.Throws<ArgumentException>(() => AdSid.ToSddl([1, 5, 0, 0]));
    }

    [Fact]
    public void RidIsTheLastSubAuthority()
    {
        Assert.Equal("513", AdSid.Rid("S-1-5-21-1-2-3-513"));
    }

    [Theory]
    [InlineData("S-1-5-21-1-2-3-513", true)]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("Domain Users", false)]
    [InlineData("S-1-", false)]
    [InlineData("", false)]
    public void SidShapeCheck(string sid, bool valid)
    {
        Assert.Equal(valid, AdSid.LooksValid(sid));
    }

    // ── Account state ────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NormalAccountIsActive()
    {
        Assert.True(AdAccount.IsActive(0x200, 0, Now));
    }

    [Fact]
    public void AccountDisableBitMeansInactive()
    {
        Assert.False(AdAccount.IsActive(0x202, 0, Now));
    }

    [Fact]
    public void NeverExpiresSentinelIsActive()
    {
        Assert.True(AdAccount.IsActive(0x200, long.MaxValue, Now));
    }

    [Fact]
    public void ExpiredAccountIsInactive()
    {
        Assert.False(AdAccount.IsActive(0x200, Now.AddDays(-1).ToFileTimeUtc(), Now));
    }

    [Fact]
    public void AccountExpiringLaterIsActive()
    {
        Assert.True(AdAccount.IsActive(0x200, Now.AddDays(1).ToFileTimeUtc(), Now));
    }

    // ── Settings rules ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, false, true)] // AD off: local sign-in can't be off
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void LocalSignInIsOnlyOffWhileAdIsOn(bool adEnabled, bool localEnabled, bool allowed)
    {
        var settings = new ActiveDirectorySettings { IsEnabled = adEnabled, LocalLoginsEnabled = localEnabled };
        Assert.Equal(allowed, ActiveDirectorySettings.LocalLoginsAllowed(settings));
    }

    [Fact]
    public void NoSettingsRowAllowsLocalSignIn()
    {
        Assert.True(ActiveDirectorySettings.LocalLoginsAllowed(null));
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(30, 30)]
    [InlineData(5000, 1440)]
    public void SyncIntervalIsClampedToFiveMinutesThroughADay(int input, int expected)
    {
        Assert.Equal(expected, ActiveDirectorySettings.ClampSyncInterval(input));
    }

    [Fact]
    public void LdapsIsTheDefault()
    {
        var settings = new ActiveDirectorySettings();
        Assert.True(settings.UseLdaps);
        Assert.Equal(636, settings.Port);
        Assert.Equal(389, new ActiveDirectorySettings { UseLdaps = false }.Port);
    }

    // ── Sync schedule ────────────────────────────────────────────────────────

    [Fact]
    public void NeverSyncedIsDue()
    {
        Assert.True(ActiveDirectorySyncService.IsDue(new ActiveDirectorySettings(), Now));
    }

    [Fact]
    public void SuccessfulSyncWaitsTheFullInterval()
    {
        var settings = new ActiveDirectorySettings
        {
            SyncIntervalMinutes = 30,
            LastSyncStartedAt = Now.AddMinutes(-20),
            LastSyncCompletedAt = Now.AddMinutes(-19)
        };
        Assert.False(ActiveDirectorySyncService.IsDue(settings, Now));
        Assert.True(ActiveDirectorySyncService.IsDue(settings, Now.AddMinutes(10)));
    }

    [Fact]
    public void FailedSyncRetriesAfterFiveMinutes()
    {
        var settings = new ActiveDirectorySettings
        {
            SyncIntervalMinutes = 60,
            LastSyncStartedAt = Now.AddMinutes(-6),
            LastSyncError = "Can't reach a domain controller."
        };
        Assert.True(ActiveDirectorySyncService.IsDue(settings, Now));
    }

    // ── Connection registry ──────────────────────────────────────────────────

    [Fact]
    public void RevokingAUserCancelsOnlyTheirConnections()
    {
        var registry = new UserConnectionRegistry();
        using var mine = registry.Register("u1", CancellationToken.None);
        using var mineToo = registry.Register("u1", CancellationToken.None);
        using var theirs = registry.Register("u2", CancellationToken.None);

        Assert.Equal(2, registry.RevokeAll("u1"));

        Assert.True(mine.Token.IsCancellationRequested);
        Assert.True(mineToo.Token.IsCancellationRequested);
        Assert.False(theirs.Token.IsCancellationRequested);
    }

    [Fact]
    public void ClosedConnectionsAreNoLongerTracked()
    {
        var registry = new UserConnectionRegistry();
        registry.Register("u1", CancellationToken.None).Dispose();

        Assert.Equal(0, registry.RevokeAll("u1"));
    }

    [Fact]
    public void LeaseFollowsTheRequestAbortToken()
    {
        var registry = new UserConnectionRegistry();
        using var request = new CancellationTokenSource();
        using var lease = registry.Register("u1", request.Token);

        request.Cancel();

        Assert.True(lease.Token.IsCancellationRequested);
    }
}
