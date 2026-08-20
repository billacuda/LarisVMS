using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_112_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 112, 0, GETUTCDATE(), 'Fixes a real bug in 0.111.0''s privacy-mask burn-in, confirmed live on real Intel/NVIDIA hardware: a camera with a Privacy zone got stuck cycling Connecting/Backoff forever, no mask or footage ever produced. Cause: pairing decode-side -hwaccel (qsv/cuda) with the plain CPU drawbox filter -- ffmpeg cannot hand hardware-decoded frames to a software-only filter without explicit conversion this pipeline never set up, so it failed immediately every attempt. Fix: RecordingSession no longer requests decode hwaccel for the masked pipeline; the encoder itself is still hardware when detected, only decode falls back to software. Needs install-node.ps1 re-run: NO. Node/NodeUpdater bumped to 0.112.0.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 112 AND Patch = 0");
        }
    }
}
