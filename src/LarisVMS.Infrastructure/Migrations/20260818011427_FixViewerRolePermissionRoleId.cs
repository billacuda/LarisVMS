using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixViewerRolePermissionRoleId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every Permission row PermissionService.SeedViewerPermissionsAsync ever wrote used the
            // literal string "Viewer" as RoleId instead of the real AspNetRoles.Id GUID — every
            // permission lookup in this app resolves role names to their real Id and compares
            // Permission.RoleId against that, so these rows have never matched anything since the
            // day this table was first seeded. Repairs any already-seeded rows in place rather than
            // leaving them dead; a no-op if none exist (fresh install, or setup never completed).
            migrationBuilder.Sql(@"
                UPDATE Permissions
                SET RoleId = r.Id
                FROM Permissions p
                INNER JOIN AspNetRoles r ON r.Name = 'Viewer'
                WHERE p.RoleId = 'Viewer'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverts to the known-broken literal string, matching this migration's own Up exactly
            // in reverse — not a data loss risk either way, since a RoleId of "Viewer" never matched
            // any real role and the rows themselves are otherwise unchanged.
            migrationBuilder.Sql(@"
                UPDATE Permissions
                SET RoleId = 'Viewer'
                FROM Permissions p
                INNER JOIN AspNetRoles r ON r.Name = 'Viewer'
                WHERE p.RoleId = r.Id AND p.Resource IN ('Logs', 'Settings', 'Views', 'Playback')");
        }
    }
}
