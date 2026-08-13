using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_40_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 40, 0, GETUTCDATE(), 'Fixed dragging the per-camera Playback timeline feeling unresponsive while the all-cameras one worked fine. The playhead-follows-playback loop calls setCenter every 500ms regardless of user interaction, which snapped the strip back to the actual playback position mid-drag, fighting a drag gesture that rarely finishes inside one 500ms window. setCenter now ignores programmatic recenters entirely while that timeline is being actively dragged. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 40 AND Patch = 0");
        }
    }
}
