using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_137_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 137, 0, GETUTCDATE(), 'Added: adaptive streaming (M18''s last open item). A live tile too small to benefit from Main resolution now shows Sub instead, cutting bandwidth/decode cost. Nodes run a second always-on ffmpeg session per camera pulling Sub (SubLiveSession, same MSE fMP4 shape as RecordingSession''s live leg). New ILiveSource interface lets LiveViewerHandler serve either source; /live/{cameraId} picks via ?role=sub, not the signed token, since stream quality is a client preference, not an authorization boundary. Each tile decides Main vs Sub from its saved grid geometry plus camera count on screen, with a manual per-tile override (Auto/HD/SD); fullscreen always forces Main. New admin toggle: Settings -> Live View -> Adaptive streaming enabled (default on) -- the node is the sole authoritative gate. Node change; install-node.ps1 re-run not needed, auto-update covers it.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 137 AND Patch = 0");
        }
    }
}
