using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_54_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 54, 0, GETUTCDATE(), 'Added an Admin dropdown to the main nav (Nodes, Node Builds, Settings) so every admin-only page lives in one findable place instead of flat top-level links -- Node Builds previously had no nav entry at all, only reachable via a help-text link from Nodes/Settings.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 54 AND Patch = 0");
        }
    }
}
