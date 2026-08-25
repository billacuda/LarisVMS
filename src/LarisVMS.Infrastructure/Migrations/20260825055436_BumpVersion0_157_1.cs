using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_157_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 157, 1, GETUTCDATE(), 'Fixed a chain of bugs blocking AI object detection end to end: missing CUDA/cuDNN dependency detection on nodes, an unfindable model file, Vision Service unable to locate ffmpeg, undiagnosable Vision Service failures (now returns the real error), and an orphaned Vision Service process surviving node restarts. install-node.ps1 now detects and auto-copies missing CUDA/cuDNN libraries. Also fixed: live-view bounding boxes never drew (JSON casing mismatch), and a segment-boundary bookkeeping bug that caused false Playback gaps on roughly 41% of recording transitions even in Continuous mode - existing Segments data repaired (8,531 rows, ~17.8 hours of footage recovered). Node/NodeUpdater bumped to 0.157.1 in lockstep; no node package rebuild strictly required unless picking up the CUDA/cuDNN/ffmpeg fixes.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 157 AND Patch = 1");
        }
    }
}
