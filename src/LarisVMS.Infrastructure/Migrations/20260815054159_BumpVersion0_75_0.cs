using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_75_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 75, 0, GETUTCDATE(), 'Fixed: exporting a range that spans a camera reassignment (0.68.0''s per-node split) always failed on its very first dispatch. SplitItemAcrossNodesAsync creates one pinned item per node but does not narrow each item''s time range, so the dispatcher was re-checking every pinned item''s segments against the whole unfiltered range and immediately hitting the multi-node hard-fail it was itself supposed to avoid. Pinned items now filter segments down to their own node before dispatching. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 75 AND Patch = 0");
        }
    }
}
