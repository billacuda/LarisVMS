using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_81_2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 81, 2, GETUTCDATE(), 'Fixed: a View cell in playback mode lost its mini-timeline when fullscreened, and did not get it back on exit. Two causes: CSS explicitly hid the timeline and playback toggle in fullscreen (inherited from the removed flat Live grid, where it made sense -- but a View cell''s timeline is inside the fullscreened element, so scrubbing a single fullscreened camera now works); and the canvas bitmap was destroyed while hidden -- a fullscreen change fires a window resize, which measured the hidden canvas at 0x0 and clamped its bitmap to 1x1, with nothing re-measuring it once visible again. Timelines now self-heal on any size change via ResizeObserver. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 81 AND Patch = 2");
        }
    }
}
