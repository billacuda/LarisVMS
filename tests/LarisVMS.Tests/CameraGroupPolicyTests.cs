using LarisVMS.Core;
using LarisVMS.Core.Entities;

namespace LarisVMS.Tests;

public class CameraGroupPolicyTests
{
    [Fact]
    public void SiteIdOfANonNestedGroupIsItself()
    {
        var site = new CameraGroup { Id = Guid.NewGuid(), ParentId = null };
        Assert.Equal(site.Id, CameraGroupPolicy.SiteIdOf(site));
    }

    [Fact]
    public void SiteIdOfANestedGroupWalksUpToTheTopLevelAncestor()
    {
        var site = new CameraGroup { Id = Guid.NewGuid(), ParentId = null };
        var building = new CameraGroup { Id = Guid.NewGuid(), ParentId = site.Id, Parent = site };
        var floor = new CameraGroup { Id = Guid.NewGuid(), ParentId = building.Id, Parent = building };

        Assert.Equal(site.Id, CameraGroupPolicy.SiteIdOf(floor));
    }

    [Fact]
    public void SiteIdOfFromLookupMatchesTheNavigationBasedResult()
    {
        var siteId = Guid.NewGuid();
        var buildingId = Guid.NewGuid();
        var floorId = Guid.NewGuid();
        var lookup = new Dictionary<Guid, Guid?> { [siteId] = null, [buildingId] = siteId, [floorId] = buildingId };

        Assert.Equal(siteId, CameraGroupPolicy.SiteIdOf(floorId, lookup));
    }

    [Fact]
    public void AnEmptySetOfGroupsAlwaysSharesOneSite()
    {
        Assert.True(CameraGroupPolicy.AllShareOneSite([], new Dictionary<Guid, Guid?>()));
    }

    [Fact]
    public void GroupsUnderTheSameSiteShareOneSite()
    {
        var siteId = Guid.NewGuid();
        var wing1 = Guid.NewGuid();
        var wing2 = Guid.NewGuid();
        var lookup = new Dictionary<Guid, Guid?> { [siteId] = null, [wing1] = siteId, [wing2] = siteId };

        Assert.True(CameraGroupPolicy.AllShareOneSite([wing1, wing2], lookup));
    }

    [Fact]
    public void GroupsUnderDifferentSitesDoNotShareOneSite()
    {
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        var wingOfA = Guid.NewGuid();
        var wingOfB = Guid.NewGuid();
        var lookup = new Dictionary<Guid, Guid?>
        {
            [siteA] = null, [siteB] = null, [wingOfA] = siteA, [wingOfB] = siteB
        };

        Assert.False(CameraGroupPolicy.AllShareOneSite([wingOfA, wingOfB], lookup));
    }

    [Fact]
    public void ASingleGroupAlwaysSharesOneSiteWithItself()
    {
        var siteId = Guid.NewGuid();
        var wing = Guid.NewGuid();
        var lookup = new Dictionary<Guid, Guid?> { [siteId] = null, [wing] = siteId };

        Assert.True(CameraGroupPolicy.AllShareOneSite([wing], lookup));
    }
}
