using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_194_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 194, 0, GETUTCDATE(), 'Added: recording failover (failover plan phase 3). Assign a recorder node a backup node on Admin - Nodes; if the node goes down, a quorum of health checks (its backup node, this server, and any assigned media proxy each probe its /health) moves its cameras to the backup for recording and live view until it returns. Footage recorded on the backup during the outage stays on the backup and plays back transparently. A central-vs-node disagreement changes nothing - a majority of responding voters must agree. Also: a manual Maintenance button per node (fails its cameras over on confirm and holds them there until cleared), a per-node Recording only switch that turns AI object detection off for that node while keeping recording and ONVIF event tagging, a hardware-mismatch warning when a backup node has different acceleration, and a NodeFailoverActivated alert condition. Media proxies now auto-update like recorder nodes (0.193.0).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 194 AND Patch = 0");
        }
    }
}
