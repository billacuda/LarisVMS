using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_34_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 34, 0, GETUTCDATE(), 'User-directed Playback redesign: timeline pinned to the bottom of the viewport, video grid auto-sized purely by camera count (1 fills the page, 2 side by side, 6 as a 3x2 grid, etc.) instead of the saved views own layout, resizing with the window. Timeline bar height halved with scale-appropriate tick labels (date/hour/minute/second depending on zoom) replacing the old two-corner-label footer. Unverified in a real browser this pass - build, tests, and node --check all pass. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 34 AND Patch = 0");
        }
    }
}
