using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_46_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 46, 0, GETUTCDATE(), 'Added day boundaries and date labels to the timeline. Below day-level zoom every tick label was time-only, so scrolling across midnight gave no sign the day had changed and a window inside a single day named no date at all. Each local midnight now draws a full-height divider, and every visible day carries a date badge at the left edge of its span, including the partial day already in progress when the window opens. Suppressed at day-level zoom and wider where tick labels are already dates, and skipped for any day too narrow on screen to hold its badge. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 46 AND Patch = 0");
        }
    }
}
