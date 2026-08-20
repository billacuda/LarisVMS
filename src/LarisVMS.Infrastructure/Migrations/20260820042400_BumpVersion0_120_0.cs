using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_120_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 120, 0, GETUTCDATE(), 'Fixed: Playback still flashed a black cell at every segment boundary despite v0.118.0''s prefetch and v0.119.0''s status-text removal. The remaining gap is structural -- switching segments tears the video element down (removeAttribute src + load), blanking it immediately, while the replacement MediaSource append and first decode still take real time even with the bytes in hand. Each tile now snapshots its last painted frame onto an overlaid canvas before the teardown and hides it only once the new segment genuinely paints (seeked/playing), so a boundary reads as a brief freeze instead of a flash to black. The snapshot copies the current digital-zoom transform; every path ending with no frame to reveal clears the overlay. Added: Play/Pause in Playback''s fullscreen control cluster -- the toolbar''s own is page-level and is not rendered while a tile holds the fullscreen layer. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 120 AND Patch = 0");
        }
    }
}
