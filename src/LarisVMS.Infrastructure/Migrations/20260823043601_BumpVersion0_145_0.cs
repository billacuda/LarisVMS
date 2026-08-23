using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_145_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 145, 0, GETUTCDATE(), 'Roles & permissions overhaul, pass 3 of 5: the full permission matrix. PermissionCatalog extended from 14 to 21 entries; Camera Groups, Users, Storage and Retention, and bookmark creation/deletion now have their own dedicated permission gates instead of sharing broader ones. RoleSeedService now seeds real Permission and CameraAccess grants for the 6 net-new built-in roles, which previously held none. Roles.Assign is tier-capped so only an Org-tier role holder can assign an Org-tier role to a user. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 145 AND Patch = 0");
        }
    }
}
