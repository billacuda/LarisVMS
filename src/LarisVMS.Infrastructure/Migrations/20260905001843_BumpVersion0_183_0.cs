using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_183_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Removed: high-resolution re-detection and high-resolution snapshots never worked
            // reliably (high-res re-detection was the largest CPU cost measured on a busy node —
            // per-trigger it software-decoded a full Main-stream keyframe). Vision debug images
            // is removed with them since its only purpose was diagnosing that feature. Pass G's
            // per-track eager Sub-stream snapshot crop is unaffected and remains the source of
            // every AI-detection snapshot.
            migrationBuilder.Sql(@"
                DELETE FROM Settings WHERE [Key] IN (
                    'Detection.EnableHighResReDetection',
                    'Detection.HiResSnapshots',
                    'Detection.EnableVisionDebugImages')");

            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 183, 0, GETUTCDATE(), 'Removed: high-resolution re-detection and high-resolution snapshots - neither worked reliably, and high-res re-detection was the largest CPU cost measured on a busy node (it software-decoded a full Main-stream keyframe per trigger). Vision debug images (Detection.EnableVisionDebugImages) removed with it since its only purpose was diagnosing that feature. The existing per-track eager Sub-stream snapshot crop is unaffected and remains the source of AI-detection snapshots. Nodes update automatically; no install-node.ps1 re-run.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 183 AND Patch = 0");
        }
    }
}
