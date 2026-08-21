using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_136_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 136, 0, GETUTCDATE(), 'Fixed: the gray-instead-of-blue timeline bug, root-caused via a concrete repro and confirmed against Segments as a real gapless recording, not a data problem. setCenter() -- the only path that moves centerMs during ordinary playback, called every 500ms by the playhead-follow loop -- only ever redrew with whatever buckets the last reload() fetched, never asked for more. At a 10-30 min zoom range, continuous playback walks the visible window off the edge of its own last-fetched coverage within minutes; the newly-scrolled-into stretch has no bucket data and reads as gray until an unrelated drag/zoom happened to trigger a fresh reload. The per-camera and all-cameras timelines reload independently, which is why one could show real colors while the other sat gray for the same instant. setCenter now tracks what its buckets actually cover and schedules a reload once the visible window drifts outside it. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 136 AND Patch = 0");
        }
    }
}
