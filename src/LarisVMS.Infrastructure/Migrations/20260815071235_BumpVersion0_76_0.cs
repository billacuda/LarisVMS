using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_76_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 76, 0, GETUTCDATE(), 'Changed: Live opens the last-watched saved view instead of the flat all-cameras grid (falls back to the first view, or a create-a-view prompt when none exist); the grid stays reachable via the picker''s All cameras option. Navbar gained emoji icons; accessibility labels added. Fixed: changing a view cell''s aspect ratio now resizes the cell, so portrait cells no longer clip the camera name; motion badges now show on Views/Play cells; the export picker lists every camera, not just the selected view''s, with clearer feedback and no double-submit; video zoom/pan drags use pointer capture, fixing an intermittent stuck drag that hijacked later gestures such as timeline scrubbing; a live tile falling behind its live edge is pulled forward every 3s so grid tiles resync after a reconnect; Live cell playback starts 30s back. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 76 AND Patch = 0");
        }
    }
}
