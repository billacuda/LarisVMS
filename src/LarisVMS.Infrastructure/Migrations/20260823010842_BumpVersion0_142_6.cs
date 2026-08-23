using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_142_6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 142, 6, GETUTCDATE(), 'Added: Admin/Nodes'' stale-footage warning now lists which cameras and when each one''s leftover footage will age out of retention on hover, instead of just a bare count with a generic message. The date is computed the same way the node''s own retention sweep actually decides it -- the newest of that camera''s segments still on this node, plus whatever retention applies to it there (camera+node override, falling back to the node''s default), so it''s exactly when that camera drops out of the warning on its own. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 142 AND Patch = 6");
        }
    }
}
