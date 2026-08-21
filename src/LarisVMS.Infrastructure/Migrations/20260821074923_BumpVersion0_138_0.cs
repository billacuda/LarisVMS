using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_138_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 138, 0, GETUTCDATE(), 'Added: Playback can jump straight to a scrub target inside a segment instead of downloading everything before it. Confirmed live against real segment sizes (24-44MB/60s at 4K/HEVC) as the actual cause of slow/stuck scrubs, not disk speed. New Mp4FragmentIndexer walks a segment''s box headers only (never sample data) to build a byte-offset/time map. Past a small threshold, the node serves just the init segment plus the nearest fragment at-or-before the target via an unsigned ?seekSeconds hint (not the signed token), reporting which instant it landed on. Falls through to whole-file below threshold or with no usable index. Also: admin-configurable segment length (Settings -> Recording, default 60s, 5-300s, camera-overridable) -- restarts recording to apply. Node change; install-node.ps1 not needed.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 138 AND Patch = 0");
        }
    }
}
