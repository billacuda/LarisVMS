using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_32_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 32, 0, GETUTCDATE(), 'Fixed a real data-loss bug: Motion-mode segments with detected motion were still being discarded. The deferred keep/discard decision anchored its lookback window to the segment end, so motion more than PostRollSeconds before a segment finished was missed - masked by the original 30s default, exposed once a shorter post-roll was configured. Now anchored to the segment start instead, so motion anywhere in the segment is credited. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 32 AND Patch = 0");
        }
    }
}
