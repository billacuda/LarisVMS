using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_132_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 132, 0, GETUTCDATE(), 'Fixed: the Playback timeline zoom reset between page loads. It was being saved correctly all along -- the bug was on restore. The saved zoom was only reapplied inside rebuildTilesFromView, which two real paths never reach: a Bookmark/Snapshot Play deep link returns early from it before the setRange call, and a visit with no remembered view never calls it at all. In both cases the timeline stayed at the 24-hour default, and the seekAll that follows then saved that default over the real zoom -- so clicking through Snapshots actively destroyed the setting rather than merely ignoring it. Both timelines now take the persisted zoom as initialRangeMs at construction. timeline.js also clamps initialRangeMs to the same limits setRange enforces, now that it comes from a stored preference. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 132 AND Patch = 0");
        }
    }
}
