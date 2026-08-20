using LarisVMS.Core.Auth;

namespace LarisVMS.Tests;

public class PermissionCatalogTests
{
    [Fact]
    public void EveryResourceActionPairIsUnique()
    {
        var pairs = PermissionCatalog.All.Select(e => (e.Resource, e.Action)).ToList();
        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    [Fact]
    public void NoEntryHasAnEmptyResourceActionOrLabel()
    {
        Assert.All(PermissionCatalog.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Resource));
            Assert.False(string.IsNullOrWhiteSpace(e.Action));
            Assert.False(string.IsNullOrWhiteSpace(e.Label));
        });
    }
}
