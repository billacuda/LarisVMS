using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NidusVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_22_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 22, 0, GETUTCDATE(), 'M8 pass 1: server-side motion detection. Nodes open a second RTSP session against a camera Sub stream and diff frames per zone; a new Zones editor (Pages/Cameras/Zones) draws polygons on a live snapshot; the timeline now shows motion in green. Known limitations: motion does not yet control recording (still continuous regardless of mode), camera-side zone push and privacy burn-in are stored but inactive, no ONVIF PullPoint event ingestion yet. Unverified against a real camera/browser - flagged the same way M7 pass 1 was. THIS IS A NidusVMS.Node CHANGE - re-run install-node.ps1 on every recorder node. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 22 AND Patch = 0");
        }
    }
}
