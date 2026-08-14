using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_48_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 48, 0, GETUTCDATE(), 'Recorder node auto-update: upload a new LarisVMS.Node.exe build once on Admin -> Node Builds, and every node whose reported version is older picks it up on its own next heartbeat, downloads it, verifies its SHA-256, and swaps its own running binary via a rebuilt LarisVMS.NodeUpdater.exe (direct port of dploid.Agent/dploid.AgentUpdater) -- no more manually re-running build-node.ps1/install-node.ps1 per machine. Gated by a new global Admin -> Settings toggle (on by default). Also fixes a version-bookkeeping gap: LarisVMS.Web''s own csproj Version was never bumped alongside the previous 0.47.0 release. See CHANGELOG for full details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 48 AND Patch = 0");
        }
    }
}
