using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_207_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 207, 0, GETUTCDATE(), 'Fixed: live view freezing for 20s-3min at a time then reconnecting every tile at once, traced to thread-pool starvation in the node process (not the browser, network or decode load) and mitigated by raising the pool''s minimum thread count; AI detection boxes running ahead of or behind the objects they mark, now held back by a measured video latency instead of a fixed constant, and interpolated between detection ticks (~7fps) so they move smoothly at video frame rate instead of stepping; a stalled tile that could rewind ~2 minutes and take the whole session down with it. Added: live-view health telemetry (catchup/hard_resync/gap_jump/decode_health/unexpected_pause/pause_resync/box_alignment beacons, Logs -> System Logs) and a node process-health heartbeat, both added specifically to diagnose these issues and left in place for the next one.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 207 AND Patch = 0");
        }
    }
}
