using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_161_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 161, 0, GETUTCDATE(), 'Snapshot correctness pass (pass 2 of the detection/hardware-acceleration overhaul). One snapshot per object instead of several when D-FINE''s per-frame class guess flickers (cat/dog/cow/horse), via a new per-track label arbiter. Snapshot Playback links now start from the recording''s own pre-roll instead of the exact detection instant (new SnapshotDto.PlayFromUtc, separate from the thumbnail''s own AtUtc). MotionSpan rows now expire with their footage: a new retention sweep, an immediate query-time guard on the Snapshots page, and a node-side sweep that self-heals orphaned cached snapshot image files.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 161 AND Patch = 0");
        }
    }
}
