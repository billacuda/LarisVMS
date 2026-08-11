using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_26_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 26, 0, GETUTCDATE(), 'M8 pass 5: fixes why the timeline was still blue-only after 0.25.0 - reporting still required confirmed motion (held for startAfter), but retention never did, so frequent short bursts were correctly retained without ever producing a reportable span. Checkpoints now report on raw recent activity, matching retention. Also: the timeline no longer freezes when only the starred camera stalls (falls back to any playing tile), and Playback now streams segments into the decoder incrementally instead of waiting for the whole file, which should both start video sooner and tighten cross-camera sync - drifted tiles are also actively nudged back in line every 500ms. This is the riskiest playback change since the original MSE design; unverified against real footage. THIS IS A NidusVMS.Node CHANGE - re-run install-node.ps1 on every recorder node. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 26 AND Patch = 0");
        }
    }
}
