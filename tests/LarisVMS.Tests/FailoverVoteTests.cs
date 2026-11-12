using System.Text.Json;
using LarisVMS.Core.Dtos;
using LarisVMS.Web.Services;

namespace LarisVMS.Tests;

/// <summary>
/// Failover quorum votes count only from voters given that node (its backup node, or a proxy
/// assigned to it), and a vote dated in the future doesn't stay fresh forever.
/// </summary>
public class FailoverVoteTests
{
    private static readonly Guid Subject = Guid.NewGuid();
    private static readonly Guid Backup = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private static string Reports(params NodePartnerHealthReport[] reports) => JsonSerializer.Serialize(reports);

    [Fact]
    public void OnlyTheAssignedVoterCounts()
    {
        var now = DateTime.UtcNow;
        var voters = new[]
        {
            (Backup, (string?)Reports(new NodePartnerHealthReport(Subject, true, now))),
            (Stranger, (string?)Reports(new NodePartnerHealthReport(Subject, false, now))),
        };

        var map = RecordingFailoverService.CollectReports(voters, (voter, subject) => voter == Backup && subject == Subject, now);

        var vote = Assert.Single(map[Subject]);
        Assert.True(vote.running);
    }

    [Fact]
    public void AVoteFromTheFutureIsIgnoredAndSlightSkewIsClamped()
    {
        var now = DateTime.UtcNow;
        var voters = new[]
        {
            (Backup, (string?)Reports(
                new NodePartnerHealthReport(Subject, false, now.AddDays(1)),
                new NodePartnerHealthReport(Subject, true, now.AddSeconds(30)))),
        };

        var map = RecordingFailoverService.CollectReports(voters, (_, _) => true, now);

        var vote = Assert.Single(map[Subject]);
        Assert.True(vote.running);
        Assert.Equal(now, vote.at);
    }
}
