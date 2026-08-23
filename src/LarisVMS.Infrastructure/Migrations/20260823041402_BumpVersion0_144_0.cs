using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_144_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 144, 0, GETUTCDATE(), 'Roles & permissions overhaul, pass 2 of 5: scope-tier enforcement on Camera Access grants. A Group-tier role can no longer be granted every camera or a whole site, only a narrower group or a single camera; a Site-tier role can no longer be granted every camera. Org-tier roles are unrestricted. A role with no RoleProfile row defaults to the most restrictive tier, and only gates new grants going forward. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 144 AND Patch = 0");
        }
    }
}
