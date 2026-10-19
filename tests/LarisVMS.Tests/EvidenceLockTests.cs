using Microsoft.EntityFrameworkCore;
using LarisVMS.Core;
using LarisVMS.Core.Dtos;
using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Core.Interfaces;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;
using LarisVMS.Node;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>Evidence locks: when a lock protects footage, the service rules (durations, ranges, who may
/// remove, the 24-hour grace period, audit), and that every retention path leaves locked footage alone.</summary>
public class EvidenceLockTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<(string Action, string? Details)> Entries { get; } = [];

        public Task LogAsync(string action, string? userId, string? userName, string? ipAddress, string? details = null, CancellationToken ct = default)
        {
            Entries.Add((action, details));
            return Task.CompletedTask;
        }
    }

    private static ApplicationDbContext NewDb() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Camera AddCamera(ApplicationDbContext db, string name = "Back yard")
    {
        var c = new Camera { Id = Guid.NewGuid(), Name = name, Host = "10.0.0.1", DeviceServiceUri = "http://10.0.0.1/onvif/device_service", IsEnabled = true };
        db.Cameras.Add(c);
        db.SaveChanges();
        return c;
    }

    private static readonly EvidenceLockActor Alice = new("alice", "Alice", IsSuperAdmin: false, null);
    private static readonly EvidenceLockActor Bob = new("bob", "Bob", IsSuperAdmin: false, null);
    private static readonly EvidenceLockActor Admin = new("root", "Admin", IsSuperAdmin: true, null);

    private static CreateEvidenceLockRequest Request(Guid cameraId, string duration = "7d", DateTime? until = null, double hoursAgo = 2)
    {
        var to = DateTime.UtcNow.AddHours(-hoursAgo);
        return new CreateEvidenceLockRequest([cameraId], to.AddMinutes(-10), to, duration, until, "Case 42");
    }

    // ── Rules ─────────────────────────────────────────────────────────────

    [Fact]
    public void ProtectionFollowsExpiryAndTheRemovalGrace()
    {
        var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(EvidenceLockRules.IsProtecting(expiresAtUtc: null, removedAtUtc: null, now));            // indefinite
        Assert.True(EvidenceLockRules.IsProtecting(now.AddMinutes(1), null, now));                          // not yet expired
        Assert.False(EvidenceLockRules.IsProtecting(now.AddMinutes(-1), null, now));                        // expired: no grace
        Assert.True(EvidenceLockRules.IsProtecting(null, now.AddHours(-23), now));                          // removed, within 24 h
        Assert.False(EvidenceLockRules.IsProtecting(null, now.AddHours(-25), now));                         // removed, grace over
        Assert.True(EvidenceLockRules.IsProtecting(now.AddHours(-48), now.AddHours(-1), now));              // removal grace wins over a past expiry
    }

    [Fact]
    public void StatusReflectsRemovalAndExpiry()
    {
        var now = DateTime.UtcNow;
        Assert.Equal(EvidenceLockStatus.Active, EvidenceLockRules.StatusOf(new EvidenceLock(), now));
        Assert.Equal(EvidenceLockStatus.Expired, EvidenceLockRules.StatusOf(new EvidenceLock { ExpiresAtUtc = now.AddDays(-1) }, now));
        Assert.Equal(EvidenceLockStatus.RemovedInGrace, EvidenceLockRules.StatusOf(new EvidenceLock { RemovedAtUtc = now.AddHours(-2) }, now));
        Assert.Equal(EvidenceLockStatus.Released, EvidenceLockRules.StatusOf(new EvidenceLock { RemovedAtUtc = now.AddDays(-2) }, now));
    }

    [Fact]
    public void ProtectedFootageMatchesOverlapsAndFilesWithAnAllowance()
    {
        var cam = Guid.NewGuid();
        var from = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);
        var locked = new ProtectedFootage([new NodeConfigProtectedRangeDto(cam, from, from.AddMinutes(10))]);

        Assert.True(locked.Covers(cam, from.AddMinutes(-1), from.AddMinutes(1)));
        Assert.False(locked.Covers(cam, from.AddMinutes(11), from.AddMinutes(12)));
        Assert.False(locked.Covers(Guid.NewGuid(), from, from.AddMinutes(10)));
        // A file last written 12 minutes after the range ended may still start inside it.
        Assert.True(locked.CoversFile(cam, from.AddMinutes(22)));
        Assert.False(locked.CoversFile(cam, from.AddMinutes(40)));
    }

    [Theory]
    [InlineData("1d", 1.0)]
    [InlineData("30d", 30.0)]
    public void DurationsMapToAnExpiry(string duration, double days)
    {
        var now = DateTime.UtcNow;
        Assert.Equal(now.AddDays(days), EvidenceLockService.ResolveExpiry(duration, null, now));
    }

    [Fact]
    public void IndefiniteHasNoExpiryAndCustomNeedsAFutureDate()
    {
        var now = DateTime.UtcNow;
        Assert.Null(EvidenceLockService.ResolveExpiry("indefinite", null, now));
        Assert.Throws<EvidenceLockValidationException>(() => EvidenceLockService.ResolveExpiry("custom", null, now));
        Assert.Throws<EvidenceLockValidationException>(() => EvidenceLockService.ResolveExpiry("custom", now.AddDays(-1), now));
        Assert.Equal(now.AddDays(90), EvidenceLockService.ResolveExpiry("custom", now.AddDays(90), now));
        Assert.Throws<EvidenceLockValidationException>(() => EvidenceLockService.ResolveExpiry("forever-ish", null, now));
    }

    // ── Service ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatingLocksOnePerCameraAndAuditsIt()
    {
        using var db = NewDb();
        var a = AddCamera(db, "A");
        var b = AddCamera(db, "B");
        var audit = new RecordingAudit();
        var service = new EvidenceLockService(db, audit);
        var to = DateTime.UtcNow.AddHours(-1);

        var created = await service.CreateAsync(new CreateEvidenceLockRequest([a.Id, b.Id], to.AddMinutes(-5), to, "indefinite", null, "Break-in"), Alice);

        Assert.Equal(2, created.Count);
        Assert.All(created, l => Assert.Null(l.ExpiresAtUtc));
        Assert.Equal(2, audit.Entries.Count(e => e.Action == "EvidenceLock.Create"));
        Assert.Contains(audit.Entries, e => e.Details!.Contains("Break-in") && e.Details.Contains("indefinitely"));
    }

    [Fact]
    public async Task BadRangesAndMissingReasonsAreRejected()
    {
        using var db = NewDb();
        var cam = AddCamera(db);
        var service = new EvidenceLockService(db, new RecordingAudit());
        var now = DateTime.UtcNow;

        await Assert.ThrowsAsync<EvidenceLockValidationException>(() =>
            service.CreateAsync(new CreateEvidenceLockRequest([cam.Id], now.AddHours(-1), now.AddHours(-2), "7d", null, "x"), Alice));
        await Assert.ThrowsAsync<EvidenceLockValidationException>(() =>
            service.CreateAsync(new CreateEvidenceLockRequest([cam.Id], now.AddHours(-1), now.AddHours(2), "7d", null, "x"), Alice));
        await Assert.ThrowsAsync<EvidenceLockValidationException>(() =>
            service.CreateAsync(new CreateEvidenceLockRequest([cam.Id], now.AddDays(-9), now.AddHours(-1), "7d", null, "x"), Alice));
        await Assert.ThrowsAsync<EvidenceLockValidationException>(() =>
            service.CreateAsync(new CreateEvidenceLockRequest([cam.Id], now.AddHours(-2), now.AddHours(-1), "7d", null, "  "), Alice));
    }

    [Fact]
    public async Task OnlyTheCreatorOrASuperAdminCanRemove()
    {
        using var db = NewDb();
        var cam = AddCamera(db);
        var audit = new RecordingAudit();
        var service = new EvidenceLockService(db, audit);
        var created = (await service.CreateAsync(Request(cam.Id), Alice))[0];

        Assert.False((await service.ListAsync(null, null, null, false, Bob))[0].CanManage);
        await Assert.ThrowsAsync<EvidenceLockForbiddenException>(() => service.RemoveAsync(created.Id, "not mine", Bob));

        var removedByAdmin = await service.RemoveAsync(created.Id, "Case closed", Admin);
        Assert.Equal("RemovedInGrace", removedByAdmin.Status);
        Assert.Contains(audit.Entries, e => e.Action == "EvidenceLock.Remove" && e.Details!.Contains("Case closed"));
    }

    [Fact]
    public async Task ARemovedLockKeepsProtectingAndCanBeRestored()
    {
        using var db = NewDb();
        var cam = AddCamera(db);
        var service = new EvidenceLockService(db, new RecordingAudit());
        var created = (await service.CreateAsync(Request(cam.Id), Alice))[0];

        await service.RemoveAsync(created.Id, "Oops", Alice);
        Assert.Single(await service.GetProtectedRangesAsync(null)); // still protected during the grace period

        var restored = await service.RestoreAsync(created.Id, Alice);
        Assert.Equal("Active", restored.Status);
        Assert.Null(restored.RemovedAtUtc);
    }

    [Fact]
    public async Task AfterTheGracePeriodTheFootageIsNoLongerProtectedAndTheSweepAuditsIt()
    {
        using var db = NewDb();
        var cam = AddCamera(db);
        var audit = new RecordingAudit();
        var service = new EvidenceLockService(db, audit);
        var created = (await service.CreateAsync(Request(cam.Id), Alice))[0];
        await service.RemoveAsync(created.Id, "Done", Alice);

        var row = await db.EvidenceLocks.SingleAsync();
        row.RemovedAtUtc = DateTime.UtcNow.AddHours(-25);
        await db.SaveChangesAsync();

        Assert.Empty(await service.GetProtectedRangesAsync(null));
        await Assert.ThrowsAsync<EvidenceLockValidationException>(() => service.RestoreAsync(created.Id, Alice));

        await EvidenceLockSweepService.SweepAsync(db, audit, DateTime.UtcNow, CancellationToken.None);
        await EvidenceLockSweepService.SweepAsync(db, audit, DateTime.UtcNow, CancellationToken.None);
        Assert.Single(audit.Entries, e => e.Action == "EvidenceLock.GraceEnded");
    }

    [Fact]
    public async Task TheSweepAuditsAnExpiryOnce()
    {
        using var db = NewDb();
        var cam = AddCamera(db);
        var audit = new RecordingAudit();
        await new EvidenceLockService(db, audit).CreateAsync(Request(cam.Id, "1d"), Alice);

        await EvidenceLockSweepService.SweepAsync(db, audit, DateTime.UtcNow.AddDays(2), CancellationToken.None);
        await EvidenceLockSweepService.SweepAsync(db, audit, DateTime.UtcNow.AddDays(3), CancellationToken.None);

        Assert.Single(audit.Entries, e => e.Action == "EvidenceLock.Expired");
    }

    // ── Retention honours locks ───────────────────────────────────────────

    [Fact]
    public async Task ServerExpiredListSkipsLockedFootage()
    {
        using var db = NewDb();
        var node = new LarisVMS.Core.Entities.Node { Id = Guid.NewGuid(), Name = "n", ApiKeyHash = "h", MediaSigningKey = "k" };
        var cam = AddCamera(db);
        db.Nodes.Add(node);
        var settings = new SettingsResolver(db);
        await settings.SetOverrideAsync(SettingScope.Node, node.Id, "Retention.Days", "14");

        var old = DateTime.UtcNow.AddDays(-40);
        db.Segments.AddRange(
            new Segment { CameraId = cam.Id, NodeId = node.Id, StartUtc = old.AddSeconds(-60), EndUtc = old, FilePath = @"D:\v\locked.mp4" },
            new Segment { CameraId = cam.Id, NodeId = node.Id, StartUtc = old.AddHours(-2), EndUtc = old.AddHours(-2).AddSeconds(60), FilePath = @"D:\v\free.mp4" });
        db.EvidenceLocks.Add(new EvidenceLock
        {
            Id = Guid.NewGuid(), CameraId = cam.Id, FromUtc = old.AddMinutes(-5), ToUtc = old.AddMinutes(5), Reason = "r", CreatedByUserId = "alice",
        });
        await db.SaveChangesAsync();

        var expired = await new NodeService(db, settings).ListExpiredSegmentsAsync(node.Id, 100);

        Assert.Equal(new[] { @"D:\v\free.mp4" }, expired.Select(e => e.FilePath));
    }

    [Fact]
    public void QuotaNeverPicksALockedFileButStillCountsIt()
    {
        var dir = Directory.CreateTempSubdirectory("lv-quota-");
        try
        {
            FileInfo Make(string name, int kb, int minutesAgo)
            {
                var path = Path.Combine(dir.FullName, name);
                File.WriteAllBytes(path, new byte[kb * 1024]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
                return new FileInfo(path);
            }
            var oldestLocked = Make("a.mp4", 10, 300);
            var next = Make("b.mp4", 10, 200);
            var newest = Make("c.mp4", 10, 100);

            var picked = StorageManager.SelectQuotaEvictions([oldestLocked, next, newest], 15 * 1024, f => f.Name == "a.mp4");

            Assert.Equal(new[] { "b.mp4", "c.mp4" }, picked.Select(f => f.Name));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task BookmarksInsideLockedFootageAreKept()
    {
        using var db = NewDb();
        var cam = AddCamera(db);
        var lockedAt = DateTime.UtcNow.AddDays(-60);
        db.Segments.AddRange(
            new Segment { CameraId = cam.Id, NodeId = Guid.NewGuid(), StartUtc = lockedAt, EndUtc = lockedAt.AddMinutes(1), FilePath = "locked.mp4" },
            new Segment { CameraId = cam.Id, NodeId = Guid.NewGuid(), StartUtc = DateTime.UtcNow.AddDays(-1), EndUtc = DateTime.UtcNow.AddDays(-1).AddMinutes(1), FilePath = "recent.mp4" });
        db.EvidenceLocks.Add(new EvidenceLock { Id = Guid.NewGuid(), CameraId = cam.Id, FromUtc = lockedAt, ToUtc = lockedAt.AddMinutes(1), Reason = "r", CreatedByUserId = "alice" });
        db.Bookmarks.AddRange(
            new Bookmark { Id = Guid.NewGuid(), CameraId = cam.Id, TimestampUtc = lockedAt.AddSeconds(30), Note = "in the lock", CreatedByUserId = "u" },
            new Bookmark { Id = Guid.NewGuid(), CameraId = cam.Id, TimestampUtc = DateTime.UtcNow.AddDays(-30), Note = "gone with its footage", CreatedByUserId = "u" });
        await db.SaveChangesAsync();

        await BookmarkRetentionService.SweepAsync(db, CancellationToken.None);

        Assert.Equal(new[] { "in the lock" }, db.Bookmarks.Select(b => b.Note));
    }

    [Fact]
    public async Task ACameraWithLockedFootageCantBeDeleted()
    {
        using var db = NewDb();
        var cam = AddCamera(db);
        db.EvidenceLocks.Add(new EvidenceLock { Id = Guid.NewGuid(), CameraId = cam.Id, FromUtc = DateTime.UtcNow.AddHours(-1), ToUtc = DateTime.UtcNow, Reason = "r", CreatedByUserId = "alice" });
        await db.SaveChangesAsync();

        var cameras = new CameraService(db, () => throw new InvalidOperationException("no HTTP in this test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cameras.DeleteAsync(cam.Id));
    }
}
