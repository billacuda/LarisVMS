using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_167_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 167, 1, GETUTCDATE(), 'Merged the Grid and Polygon zone editors onto one Zones page with a single toggle, per direct feedback that two separate pages made no sense - toggling now both switches which editor is shown and immediately activates that method, removing the separate not-currently-active banner and Make this the active method button from the earlier two-page design. Added an actual Save button for grid cell edits (clicks/size/sensitivity now accumulate locally and save together, like the polygon zone form already does) after feedback that clicking cells had no visible save step. New cameras now default to Grid mode (previously Polygon); existing cameras are unaffected since AddMotionGridColumns already backfilled them to Polygon explicitly. Deleted the standalone MotionGrid page.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 167 AND Patch = 1");
        }
    }
}
