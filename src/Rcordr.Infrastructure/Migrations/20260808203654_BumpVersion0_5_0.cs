using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rcordr.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_5_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 5, 0, GETUTCDATE(), 'Node IP address tracking (server-captured, not self-reported); real stream resolution/codec read from ffmpeg''s own stderr instead of ONVIF''s unreliable VideoEncoderConfiguration; per-stream enable/disable and rename on Cameras/Edit, surviving re-probe; fixed node Version never updating past first registration; fixed install-node.ps1''s upgrade path (no longer deletes the service, reuses already-installed ffmpeg, kills orphaned ffmpeg processes before copying).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 5 AND Patch = 0");
        }
    }
}
