using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_148_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 148, 0, GETUTCDATE(), 'A camera can now belong to more than one camera group at once, as long as they all share one top-level site. Replaces the old single Camera.GroupId with a many-to-many membership (existing assignments migrated automatically). Cameras > Groups gained a Manage cameras popup (checkbox multi-select per group, replacing the one-at-a-time picker); Cameras/Edit''s Group field is now multi-select; Camera Access group-scoped grants now reach a camera through any of its group memberships. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 148 AND Patch = 0");
        }
    }
}
