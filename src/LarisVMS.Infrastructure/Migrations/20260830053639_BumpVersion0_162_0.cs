using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_162_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 162, 0, GETUTCDATE(), 'Pass 3a of the detection/hardware-acceleration overhaul: a new Main-stream fragment ring buffer (MainFrameRingBuffer) on each node, fed from RecordingSessions own existing live-tee fanout, keeps a short in-memory window of recent decodable video for a later on-demand fetch. New loopback-only node route GET /internal/main-frame/{cameraId} hands buffered bytes to the Vision Service sibling process; the node itself never decodes anything. This replaces the lazy seek-into-an-already-written-segment approach behind the Segment.DurationMs drift bug worked around in v0.161.6. This checkpoint only buffers and serves bytes; nothing decodes or uses them yet, that is the next checkpoint (pass 3b).')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 162 AND Patch = 0");
        }
    }
}
