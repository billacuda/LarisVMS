using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_130_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 130, 0, GETUTCDATE(), 'Fixed: Snapshots and Audit Logs date filters were off by the UTC offset, cutting the selected day short -- confirmed at UTC-7 where results stopped at 4:59 PM (exactly 23:59:59Z). A date input posts a bare yyyy-MM-dd with no timezone and both pages passed it through as Unspecified, which everything downstream treats as already-UTC, so a local day became a UTC day. New LocalDateFilter converts the picked date to the UTC instants bounding that day in the server''s own zone. Fixed: a Bookmark/Snapshot Play link landed at the start of the containing minute -- under MSE the browser clamps a seek to the seekable range derived from what is buffered, so a target 45s into a 60s segment was clamped back to its start. Playback now waits for the requested instant to be buffered before seeking. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 130 AND Patch = 0");
        }
    }
}
