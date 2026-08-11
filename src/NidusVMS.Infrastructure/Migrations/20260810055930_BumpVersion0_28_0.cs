using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_28_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 28, 0, GETUTCDATE(), 'Investigated why the timeline showed zero motion since M8 shipped, with direct database and process-level access to the live deployment. The pipeline was not broken: confirmed the motion ffmpeg process running correctly against each camera Sub stream, and confirmed MotionDetector/ZoneRasterizer produce correct scores when run directly against real captured camera bytes. The default 15% zone sensitivity was simply unreachable in practice - the highest score observed across real frames, including compression-artifact spikes, was 0.04%. Default lowered to 3% for new zones. Does NOT retroactively update zones already saved with the old default - existing zones need sensitivity lowered manually via Pages/Cameras/Zones. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 28 AND Patch = 0");
        }
    }
}
