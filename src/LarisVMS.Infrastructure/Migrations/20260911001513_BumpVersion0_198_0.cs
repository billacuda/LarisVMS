using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_198_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 198, 0, GETUTCDATE(), 'Fixed: live view spent its time catching up, pausing, then catching up again while recorded playback stayed smooth. The recorder''s live stream emitted one fMP4 fragment per camera keyframe, delivering video to the browser in 2-4s bursts, and the client chased the live edge with almost no buffer using a blunt fixed 1.5x speed boost that overshot, stalled, and fell behind on every keyframe. The live stream (only - recorded segment files are unchanged) is now muxed to flush a fragment about every 500ms, and the client positions playback ~2s behind the live edge on connect and trims speed gently (max 1.25x) to hold it there. Live view now sits ~2-4s behind real time and plays continuously. Also fixed: a brief network stall on one live tile used to drop a queued fragment and punch an unrecoverable hole in that browser''s stream - the node now closes that one viewer''s socket cleanly for a fresh reconnect instead, leaving recording and other viewers unaffected.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 198 AND Patch = 0");
        }
    }
}
