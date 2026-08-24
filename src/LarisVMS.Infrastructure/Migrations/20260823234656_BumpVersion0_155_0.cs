using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_155_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 155, 0, GETUTCDATE(), 'Fixed: changing an already-recording camera''s ONVIF Device Service URI never took effect - it kept recording/live-viewing from the old source until deleted and re-added. NodeWorker.Reconcile()''s Main-stream branch never compared the camera''s current RTSP URI against the one its running session was started with, unlike the Sub/adaptive-stream branch which already did. Now runs the same signature-and-restart check, so a URL change is picked up on the next reconcile (~30s). LarisVMS.Node change - install-node.ps1 re-run needed on every recorder.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 155 AND Patch = 0");
        }
    }
}
