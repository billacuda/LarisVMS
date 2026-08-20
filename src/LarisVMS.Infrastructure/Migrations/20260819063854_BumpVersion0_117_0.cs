using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_117_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 117, 0, GETUTCDATE(), 'M18: snapshots browser. New Pages/Snapshots lists every motion event (zone, camera-pushed, custom tag, object detection) as a paged thumbnail grid, newest first, filterable by camera/date. No new table or capture pipeline: reuses MotionSpans directly and pulls each thumbnail live from /playback-thumbnail, the same historical frame-extraction path Playback''s own hover thumbnails already use. Label/color/emoji resolve with the same precedence TimelineService''s own bucket coloring applies (custom EventTagRule color wins, then a detected class''s admin-configurable color, then plain motion), so a card can never drift from how that instant renders on the timeline. Play links reuse Bookmarks'' own ?cameraId=&atUtc= deep link into Playback. Shared across everyone with Playback.View, same visibility as Bookmarks/Exports -- no per-camera narrowing. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 117 AND Patch = 0");
        }
    }
}
