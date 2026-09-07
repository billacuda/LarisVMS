using Microsoft.EntityFrameworkCore;
using LarisVMS.Infrastructure.Data;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Admin → Settings → Nodes now calls GetOrCreateNodeRegistrationKeyAsync on GET (rather than a bare
/// settings read) so the field is never rendered empty on a deployment whose setup predates the
/// wizard's Node step or whose Settings row was removed — the case that made the key box look blank.
/// </summary>
public class NodeRegistrationKeyTests
{
    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task GetOrCreateGeneratesAndPersistsAKeyWhenNoneExists()
    {
        var db = NewDb();
        var setup = new SetupService(db, null!, null!, null!, null!);

        var key = await setup.GetOrCreateNodeRegistrationKeyAsync(CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(key));
        var stored = await db.Settings.SingleAsync(s => s.Key == "Node.RegistrationKey");
        Assert.Equal(key, stored.Value);
    }

    [Fact]
    public async Task GetOrCreateReturnsTheExistingKeyUnchanged()
    {
        var db = NewDb();
        var setup = new SetupService(db, null!, null!, null!, null!);

        var first = await setup.GetOrCreateNodeRegistrationKeyAsync(CancellationToken.None);
        var second = await setup.GetOrCreateNodeRegistrationKeyAsync(CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, await db.Settings.CountAsync(s => s.Key == "Node.RegistrationKey"));
    }
}
