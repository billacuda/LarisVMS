using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_35_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 35, 0, GETUTCDATE(), 'M8 pass 6: ONVIF PullPoint event ingestion. A node now subscribes to a camera Events service and polls for notifications; motion-classified events drive the same Motion-mode recording gate and MotionSpans timeline coloring a ServerMotion zone already does, reusing MotionHysteresis. Every raw notification is also logged to a new CameraEvents table. Zone-side push and privacy burn-in remain deferred. Unverified against a real camera. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 35 AND Patch = 0");
        }
    }
}
