using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Security;
using LarisVMS.Infrastructure.Services;
using NodeEntity = LarisVMS.Core.Entities.Node;

namespace LarisVMS.Tests;

/// <summary>Failover plan phase 5a/5b: the rolling check-in nonce truth table, bearer-secret
/// rotation, and the admin auth-reset recovery path — all off NodeService against an in-memory DB,
/// the same shape as NodeServiceStorageConfigTests.</summary>
public class NodeServiceReplayHardeningTests
{
    private static (NodeService Service, ApplicationDbContext Db, Guid NodeId) Seed(Action<NodeEntity>? configure = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new ApplicationDbContext(options);
        var node = new NodeEntity
        {
            Id = Guid.NewGuid(), Name = "node-1", ApiKeyHash = SecretHash.Hash("orig-secret"),
            MediaSigningKey = "key", CreatedAt = DateTime.UtcNow,
        };
        configure?.Invoke(node);
        db.Add(node);
        db.SaveChanges();
        return (new NodeService(db, new SettingsResolver(db)), db, node.Id);
    }

    [Fact]
    public async Task FirstCheckInWithNoStoredNonceIssuesOneAndPasses()
    {
        var (service, db, nodeId) = Seed();

        var result = await service.ApplyCheckInSecurityAsync(nodeId, presentedNonce: null);

        Assert.False(result.ReplayRejected);
        Assert.False(string.IsNullOrEmpty(result.NextNonce));
        Assert.Equal(result.NextNonce, (await db.Nodes.FindAsync(nodeId))!.CheckInNonce);
    }

    [Fact]
    public async Task CorrectEchoPassesAndRollsToAFreshNonce()
    {
        var (service, _, nodeId) = Seed();
        var first = await service.ApplyCheckInSecurityAsync(nodeId, null);

        var second = await service.ApplyCheckInSecurityAsync(nodeId, first.NextNonce);

        Assert.False(second.ReplayRejected);
        Assert.NotEqual(first.NextNonce, second.NextNonce);
    }

    [Fact]
    public async Task AbsentEchoAgainstAStoredNonceIsAllowedAndClearsIt()
    {
        var (service, db, nodeId) = Seed(n => n.CheckInNonce = "stored-value");

        var result = await service.ApplyCheckInSecurityAsync(nodeId, presentedNonce: null);

        // A not-yet-upgraded node: allowed through, and a *new* nonce is issued (the old one is not
        // simply left in place forever).
        Assert.False(result.ReplayRejected);
        Assert.False(string.IsNullOrEmpty(result.NextNonce));
        Assert.NotEqual("stored-value", (await db.Nodes.FindAsync(nodeId))!.CheckInNonce);
    }

    [Fact]
    public async Task WrongEchoIsRejectedAsAReplayAndLeavesTheStoredNonceIntact()
    {
        var (service, db, nodeId) = Seed(n => n.CheckInNonce = "the-real-nonce");

        var result = await service.ApplyCheckInSecurityAsync(nodeId, presentedNonce: "a-stale-nonce");

        Assert.True(result.ReplayRejected);
        Assert.Null(result.NextNonce);
        Assert.Equal("the-real-nonce", (await db.Nodes.FindAsync(nodeId))!.CheckInNonce);
    }

    [Fact]
    public async Task PendingRotationIssuesANewSecretAndPreservesTheOldHashForTheGraceWindow()
    {
        var (service, db, nodeId) = Seed(n => n.PendingSecretRotation = true);
        var originalHash = (await db.Nodes.FindAsync(nodeId))!.ApiKeyHash;

        var result = await service.ApplyCheckInSecurityAsync(nodeId, null);

        Assert.False(string.IsNullOrEmpty(result.NewSecret));
        var node = (await db.Nodes.FindAsync(nodeId))!;
        Assert.Equal(originalHash, node.PreviousApiKeyHash);
        Assert.Equal(SecretHash.Hash(result.NewSecret!), node.ApiKeyHash);
        Assert.False(node.PendingSecretRotation);
        Assert.NotNull(node.ApiKeyRotatedAt);

        // Both secrets authenticate during the grace window...
        Assert.NotNull(await service.AuthenticateAsync(nodeId.ToString(), "orig-secret", null));
        // ...until the node first uses the new one, which closes it.
        Assert.NotNull(await service.AuthenticateAsync(nodeId.ToString(), result.NewSecret!, null));
        Assert.Null((await db.Nodes.FindAsync(nodeId))!.PreviousApiKeyHash);
        Assert.Null(await service.AuthenticateAsync(nodeId.ToString(), "orig-secret", null));
    }

    [Fact]
    public async Task NoAutomaticRotationWhenSecretRotationDaysIsZeroEvenForAnAgedSecret()
    {
        var (service, _, nodeId) = Seed(n =>
        {
            n.SecretRotationDays = 0;
            n.ApiKeyRotatedAt = DateTime.UtcNow.AddDays(-999);
        });

        var result = await service.ApplyCheckInSecurityAsync(nodeId, null);

        Assert.Null(result.NewSecret);
    }

    [Fact]
    public async Task AgedSecretRotatesOnceArmed()
    {
        var (service, _, nodeId) = Seed(n =>
        {
            n.SecretRotationDays = 30;
            n.ApiKeyRotatedAt = DateTime.UtcNow.AddDays(-31);
        });

        var result = await service.ApplyCheckInSecurityAsync(nodeId, null);

        Assert.False(string.IsNullOrEmpty(result.NewSecret));
    }

    [Fact]
    public async Task ResetAuthClearsTheNonceAndFlagsReregistration()
    {
        var (service, db, nodeId) = Seed(n => n.CheckInNonce = "wedged");

        await service.ResetAuthAsync(nodeId, rotateSecretNow: false);

        var node = (await db.Nodes.FindAsync(nodeId))!;
        Assert.Null(node.CheckInNonce);
        Assert.True(node.AllowReregistration);
        Assert.False(node.PendingSecretRotation);
    }

    [Fact]
    public async Task ResetAuthWithRotateArmsTheNextCheckInToRotate()
    {
        var (service, _, nodeId) = Seed();

        await service.ResetAuthAsync(nodeId, rotateSecretNow: true);
        var result = await service.ApplyCheckInSecurityAsync(nodeId, null);

        Assert.False(string.IsNullOrEmpty(result.NewSecret));
    }
}
