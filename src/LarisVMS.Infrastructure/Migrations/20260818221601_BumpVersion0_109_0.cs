using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_109_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 109, 0, GETUTCDATE(), 'M16 viewing-experience pass (partial -- see CHANGELOG for what is deliberately deferred). Pinch-to-zoom in fullscreen (fullscreen-tile.js, shared by Live and Playback) alongside the existing wheel-zoom/drag-pan. Drag-select-to-zoom on a Playback grid cell: dragging at 1x now draws a selection rectangle and zooms to fill it, instead of only panning once already zoomed. Playback speed 1/32x-32x -- native playbackRate through 8x, a seek-driven stepped mode above that (I-frame-only decode in effect, since each seek decodes fresh from a keyframe rather than continuously). Playback event-tag toggle, off by default per-user, new timeline.js showEventTags option. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 109 AND Patch = 0");
        }
    }
}
