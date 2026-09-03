using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_174_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 174, 0, GETUTCDATE(), 'Added a per-camera AI detection orientation (Cameras > Edit, with a deployment default on Admin > Settings > Detection). Some corridor-mounted cameras advertise a landscape detection stream over ONVIF while actually sending portrait video; left on Auto the recorder squashes the real frame into the advertised shape, so the model runs on a stretched image and every snapshot crop comes off that distorted buffer. Setting Portrait corrects the dimensions before the detection profile is built. Fixed: AI detection no longer restarts a camera watch in a loop - 0.172.0 persisted a measured resolution that a camera re-probe overwrote from ONVIF on every server restart, discarding that camera object-tracking state each cycle. Added a per-camera detection cadence log line every 30 seconds.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 174 AND Patch = 0");
        }
    }
}
