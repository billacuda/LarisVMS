using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_78_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 78, 0, GETUTCDATE(), 'Fixed: regression from 0.76.0''s live-tile drift resync -- a lagging tile repeated a few seconds of already-played video every few minutes before catching up, and tiles could end up minutes apart from each other. The periodic check reused jumpToLiveEdge, which targets the start of the newest buffered range (correct for its original stall-recovery case, where currentTime is outside any buffered range) -- for an already-playing-but-lagging tile that jumped further from the live edge, not closer, so it kept re-triggering. Now nudges to just behind the true live edge instead. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 78 AND Patch = 0");
        }
    }
}
