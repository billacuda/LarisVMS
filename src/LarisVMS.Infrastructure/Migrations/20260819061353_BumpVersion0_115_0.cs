using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_115_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 115, 0, GETUTCDATE(), 'M18: basic PTZ. New OnvifPtzClient (ContinuousMove/Stop) called directly from Web, no node hop, same as camera probing. PtzService resolves PTZ XAddr from CameraCapabilities.RawProbeJson (populated since M8, previously unused) plus the Main stream''s profile token. New /api/cameras/{id}/ptz/move and /stop, gated Cameras.View + per-camera CameraAccessActions.Ptz (schema since M14, unenforced until now). Live page: a directional pad + zoom per tile, shown only when HasPtz is true. Client re-issues a held direction every 2s, always stops on release -- a dead-man''s-switch matching the device''s own 5s auto-stop. Not yet run against a real PTZ camera. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 115 AND Patch = 0");
        }
    }
}
