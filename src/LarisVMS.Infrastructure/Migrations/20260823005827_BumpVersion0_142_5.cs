using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_142_5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 142, 5, GETUTCDATE(), 'Fixed: LarisVMS.NodeUpdater.exe (the detached helper that swaps the node binary and restarts the service) only logged to console, which goes nowhere when launched from a Windows Service -- a failed swap or restart left the service stopped with zero trace of why, and SCM failure-recovery does not apply since the service stops itself deliberately. It now writes a rolling daily log next to the node''s own logs, and the final service-start step retries up to 5 times instead of one shot. Node and NodeUpdater change -- install-node.ps1 re-run IS needed on every node: NodeUpdater.exe is never part of the auto-update payload, only placed by install-node.ps1.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 142 AND Patch = 5");
        }
    }
}
