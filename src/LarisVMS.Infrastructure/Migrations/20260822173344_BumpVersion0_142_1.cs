using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_142_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 142, 1, GETUTCDATE(), 'Fixed: the Playback timeline strip and time readout sat behind the picture after jumping into the middle of a segment (opening a Snapshot or Bookmark). The node serves that as a partial fetch starting at the nearest fragment boundary; the player stored that fragment''s own media time as the segment''s wall-clock origin instead of the segment''s true start, so every wall-clock readout derived from it read early by however far into the segment the seek had landed -- showing the segment''s own start instead of the event, then snapping forward once the next segment loaded whole. The seek itself was already correct, so the video always played the right frames; only the timeline and time display lagged. Unaffected for ordinary whole-file loads. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 142 AND Patch = 1");
        }
    }
}
