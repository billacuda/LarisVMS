using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_16_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 16, 0, GETUTCDATE(), 'Fixed the real cause of Playback''s stuck-on-Loading bug: unthrottled drag-scrubbing flooded the browser''s connection pool (net::ERR_INSUFFICIENT_RESOURCES), not a hung storage read. Throttled the drag handler and added actual fetch cancellation for superseded seeks. Tiles are now click-to-select-primary on the whole cell. Added a 25s timeout on the Web-tier''s node proxy call. No LarisVMS.Node changes - no recorder-node update needed for this release. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 16 AND Patch = 0");
        }
    }
}
