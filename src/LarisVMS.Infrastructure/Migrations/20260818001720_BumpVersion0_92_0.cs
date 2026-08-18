using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_92_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 92, 0, GETUTCDATE(), 'Added: cameras are re-probed automatically once a day at a chosen time (Admin > Settings > Cameras, default 03:00 server local time, switchable off), picking up a camera that has gained, lost or re-encoded a stream without anyone pressing Re-probe. Per-stream enable/disable and custom names are carried across a probe and streams match by profile token, so re-probing an unchanged camera changes nothing. Cameras are probed one at a time rather than in parallel, since a fleet-wide burst of ONVIF conversations is what makes inexpensive cameras drop their other connections including the RTSP session being recorded; one unreachable camera does not end the pass, and each run writes a single audit entry. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 92 AND Patch = 0");
        }
    }
}
