using LarisVMS.Core.Entities;
using LarisVMS.Core.Enums;
using LarisVMS.Infrastructure.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Pure resolution logic behind CameraAccess enforcement — CameraAccessService.Resolve, tested
/// directly against already-fetched rows so the access-narrowing rules are provable without a real
/// DbContext. GetAccessibleCameraIdsAsync's own short-circuits (Administrator, unauthenticated,
/// zero-rows-means-unrestricted) are simple enough to read by inspection; this covers what happens
/// once a principal actually has rows.
/// </summary>
public class CameraAccessServiceTests
{
    private static CameraAccess Row(CameraAccessScopeType scope, Guid? scopeId, CameraAccessActions actions)
        => new() { Id = Guid.NewGuid(), PrincipalType = CameraAccessPrincipalType.User, PrincipalId = "u1", ScopeType = scope, ScopeId = scopeId, Actions = actions };

    [Fact]
    public void AnAllScopeGrantForTheRequestedActionMeansUnrestricted()
    {
        var rows = new[] { Row(CameraAccessScopeType.All, null, CameraAccessActions.View) };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [], new Dictionary<Guid, string>());

        Assert.Null(result);
    }

    [Fact]
    public void AnAllScopeGrantForADifferentActionDoesNotUnlockThisOne()
    {
        var rows = new[] { Row(CameraAccessScopeType.All, null, CameraAccessActions.Export) };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [], new Dictionary<Guid, string>());

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void ACameraScopeGrantAddsExactlyThatCamera()
    {
        var camId = Guid.NewGuid();
        var rows = new[] { Row(CameraAccessScopeType.Camera, camId, CameraAccessActions.View) };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [], new Dictionary<Guid, string>());

        Assert.Equal([camId], result);
    }

    [Fact]
    public void ACameraGrantForADifferentActionIsIgnored()
    {
        var camId = Guid.NewGuid();
        var rows = new[] { Row(CameraAccessScopeType.Camera, camId, CameraAccessActions.Playback) };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [], new Dictionary<Guid, string>());

        Assert.Empty(result!);
    }

    [Fact]
    public void AGroupGrantIncludesEveryCameraDirectlyInThatGroup()
    {
        var groupId = Guid.NewGuid();
        var camInGroup = new CameraAccessService.CameraGroupInfo(Guid.NewGuid(), "/site-a/");
        var camElsewhere = new CameraAccessService.CameraGroupInfo(Guid.NewGuid(), "/site-b/");
        var rows = new[] { Row(CameraAccessScopeType.Group, groupId, CameraAccessActions.View) };
        var paths = new Dictionary<Guid, string> { [groupId] = "/site-a/" };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [camInGroup, camElsewhere], paths);

        Assert.Contains(camInGroup.CameraId, result!);
        Assert.DoesNotContain(camElsewhere.CameraId, result!);
    }

    [Fact]
    public void AGroupGrantCascadesToDescendantGroups()
    {
        // Granting "/site-a/" must also reach a camera whose own group is "/site-a/bldg-2/" —
        // the whole reason CameraGroup carries a materialized path.
        var groupId = Guid.NewGuid();
        var camInDescendant = new CameraAccessService.CameraGroupInfo(Guid.NewGuid(), "/site-a/bldg-2/");
        var rows = new[] { Row(CameraAccessScopeType.Group, groupId, CameraAccessActions.View) };
        var paths = new Dictionary<Guid, string> { [groupId] = "/site-a/" };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [camInDescendant], paths);

        Assert.Contains(camInDescendant.CameraId, result!);
    }

    [Fact]
    public void AGroupGrantDoesNotMatchASimilarlyNamedSiblingGroup()
    {
        // "/site-a/" must not match "/site-ab/" — a naive Contains/prefix check without the
        // trailing slash convention would falsely include a sibling whose name happens to start
        // the same way.
        var groupId = Guid.NewGuid();
        var sibling = new CameraAccessService.CameraGroupInfo(Guid.NewGuid(), "/site-ab/");
        var rows = new[] { Row(CameraAccessScopeType.Group, groupId, CameraAccessActions.View) };
        var paths = new Dictionary<Guid, string> { [groupId] = "/site-a/" };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [sibling], paths);

        Assert.DoesNotContain(sibling.CameraId, result!);
    }

    [Fact]
    public void CameraAndGroupGrantsCombineViaUnion()
    {
        var directCam = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var camInGroup = new CameraAccessService.CameraGroupInfo(Guid.NewGuid(), "/site-a/");
        var rows = new[]
        {
            Row(CameraAccessScopeType.Camera, directCam, CameraAccessActions.View),
            Row(CameraAccessScopeType.Group, groupId, CameraAccessActions.View)
        };
        var paths = new Dictionary<Guid, string> { [groupId] = "/site-a/" };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [camInGroup], paths);

        Assert.Equal(2, result!.Count);
        Assert.Contains(directCam, result);
        Assert.Contains(camInGroup.CameraId, result);
    }

    [Fact]
    public void ACameraWithNoGroupIsNeverReachedByAGroupGrant()
    {
        var groupId = Guid.NewGuid();
        var ungrouped = new CameraAccessService.CameraGroupInfo(Guid.NewGuid(), null);
        var rows = new[] { Row(CameraAccessScopeType.Group, groupId, CameraAccessActions.View) };
        var paths = new Dictionary<Guid, string> { [groupId] = "/site-a/" };

        var result = CameraAccessService.Resolve(rows, CameraAccessActions.View, [ungrouped], paths);

        Assert.DoesNotContain(ungrouped.CameraId, result!);
    }
}
