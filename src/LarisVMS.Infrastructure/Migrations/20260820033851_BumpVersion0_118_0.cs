using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_118_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 118, 0, GETUTCDATE(), 'M18 follow-ups from live testing. Snapshots: fixed cropped thumbnails (object-fit:contain) and imprecise 5-min-bucketed lookup (new exact=true thumbnail path); timestamp now the event''s midpoint, not its start; new admin-configurable per-type visibility (Admin>Settings>Events). Playback: refresh now restores the last-open view (not just position/zoom); segments prefetch one ahead so faster-than-1x playback no longer stalls at each 60s boundary; fixed a race where a Bookmark/Snapshot deep link could land at the covering segment''s start instead of the exact instant; bookmarks now render as markers on the per-camera timeline. Mobile: pinch-to-zoom on both timelines; phone landscape keeps its 1/2-column stack (fitted to avoid scrolling) instead of falling back to the desktop grid; fullscreen controls no longer sit underneath the timeline. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 118 AND Patch = 0");
        }
    }
}
