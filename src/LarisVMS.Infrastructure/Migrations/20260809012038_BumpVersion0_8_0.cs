using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_8_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 8, 0, GETUTCDATE(), 'Cross-cutting UI backlog closed out: dark mode toggle button, click-to-sort and filter/search on Cameras and Nodes tables, quick enable/disable + status indicator on Cameras/Index, bulk camera re-pointing to another node, and a stale-segment warning badge for footage left behind on a camera''s previous node. See CHANGELOG for details.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 8 AND Patch = 0");
        }
    }
}
