using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_111_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 111, 0, GETUTCDATE(), 'M18 pass 1: static privacy-mask burn-in, the first real EncodePipeline consumer. A camera''s enabled Privacy zones (ZoneKind.Privacy existed unused since M8) burn in as black boxes -- each zone''s bounding box, not its exact outline -- into both recordings and live view via one shared encode ahead of the existing tee. RecordingSession switches from -c copy to real decode/filter/encode only for a camera with a Privacy zone; every other camera is unaffected. Encoder prefers hardware (NVENC, QSV, AMF) from the M17 probe, falling back to libx264. Editing Privacy zones restarts that camera''s recording session. Also fixes a latent hang: RecordingSession only killed ffmpeg on a stall, not on a plain cancellation. Needs install-node.ps1 re-run: NO. Node/NodeUpdater bumped to 0.111.0.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 111 AND Patch = 0");
        }
    }
}
