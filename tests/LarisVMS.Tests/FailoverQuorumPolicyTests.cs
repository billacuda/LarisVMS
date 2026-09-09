using LarisVMS.Core.Enums;
using LarisVMS.Web.Services;
using V = LarisVMS.Web.Services.FailoverQuorumPolicy.Vote;

namespace LarisVMS.Tests;

/// <summary>The failover quorum truth table (LarisVMS failover plan, phase 3 Verification). Pure —
/// <see cref="FailoverQuorumPolicy.Evaluate"/> has no I/O.</summary>
public class FailoverQuorumPolicyTests
{
    private static FailoverQuorumPolicy.Verdict Eval(NodeFailoverState state, params V[] votes)
        => FailoverQuorumPolicy.Evaluate(votes, state);

    [Fact]
    public void TwoOfTwoDown_FromNormal_FailsOver()
        => Assert.Equal(FailoverQuorumPolicy.Verdict.FailOver, Eval(NodeFailoverState.Normal, V.Down, V.Down));

    [Fact]
    public void TwoOfThreeDown_FromNormal_FailsOver()
        => Assert.Equal(FailoverQuorumPolicy.Verdict.FailOver, Eval(NodeFailoverState.Normal, V.Down, V.Down, V.Up));

    [Fact]
    public void ThreeOfThreeDown_FromNormal_FailsOver()
        => Assert.Equal(FailoverQuorumPolicy.Verdict.FailOver, Eval(NodeFailoverState.Normal, V.Down, V.Down, V.Down));

    [Fact]
    public void OneOneSplit_Holds()
        => Assert.Equal(FailoverQuorumPolicy.Verdict.NoChange, Eval(NodeFailoverState.Normal, V.Down, V.Up));

    [Fact]
    public void OnlyOneVoterReachable_HoldsRegardlessOfItsVote()
    {
        Assert.Equal(FailoverQuorumPolicy.Verdict.NoChange, Eval(NodeFailoverState.Normal, V.Down));
        Assert.Equal(FailoverQuorumPolicy.Verdict.NoChange, Eval(NodeFailoverState.FailedOverAway, V.Up));
    }

    [Fact]
    public void NoVoters_Holds()
        => Assert.Equal(FailoverQuorumPolicy.Verdict.NoChange, Eval(NodeFailoverState.Normal));

    [Fact]
    public void FailBackNeedsInvertedMajority()
    {
        // Currently failed over. One up vote among two = tie = hold.
        Assert.Equal(FailoverQuorumPolicy.Verdict.NoChange, Eval(NodeFailoverState.FailedOverAway, V.Up, V.Down));
        // Two up of two = strict majority = fail back.
        Assert.Equal(FailoverQuorumPolicy.Verdict.FailBack, Eval(NodeFailoverState.FailedOverAway, V.Up, V.Up));
        // Two up of three = fail back.
        Assert.Equal(FailoverQuorumPolicy.Verdict.FailBack, Eval(NodeFailoverState.FailedOverAway, V.Up, V.Up, V.Down));
    }

    [Fact]
    public void FailedOverAway_MajorityStillDown_Holds()
        => Assert.Equal(FailoverQuorumPolicy.Verdict.NoChange, Eval(NodeFailoverState.FailedOverAway, V.Down, V.Down));

    [Fact]
    public void HostingFailoverNode_ThatItselfGoesDown_FailsOver()
        => Assert.Equal(FailoverQuorumPolicy.Verdict.FailOver, Eval(NodeFailoverState.HostingFailover, V.Down, V.Down));
}
