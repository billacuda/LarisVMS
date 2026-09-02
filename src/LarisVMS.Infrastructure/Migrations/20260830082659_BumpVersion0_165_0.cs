using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_165_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 165, 0, GETUTCDATE(), 'Checkpoint 4 (3c-1): the Zones editor now shows real live video with each Motion zone washed by its own live motion score, instead of one static snapshot with fixed-opacity polygons. MotionSession tracks every zones most recent per-frame score; a new /live/{cameraId}/motion-zones WebSocket (Node, poll-driven, same never-block-the-hot-loop shape as the AI-detection overlay) plus a Web-tier relay carry it to the browser. zones-editor.js starts real live video behind its existing polygon canvas via live-view.js, falling back to the static snapshot until the first live frame arrives, or permanently on a camera that cannot stream live. Each ServerMotion zones fill opacity now rises and falls with its live score relative to its own Sensitivity; other zone kinds, which have no live score, keep their prior fixed look. This completes the planned pass 3 checkpoint sequence.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 165 AND Patch = 0");
        }
    }
}
