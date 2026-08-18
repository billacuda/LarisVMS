using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_97_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 97, 0, GETUTCDATE(), 'Fixed: the Viewer role has never had a single working permission, for any resource, since it was first seeded -- every seeded row stored the literal string Viewer as RoleId instead of the role''s real database Id, which every permission check in this app actually compares against. Invisible in practice because the only role ever really exercised is Administrator, which bypasses the Permission table entirely. A previous pass (0.94.0) fixed a resource-naming mismatch in the same method and reported it resolved; that fix was real but sat on top of this deeper bug and made no observable difference -- corrected in place in that entry. Fixed by seeding the role''s real Id. A migration repairs any rows an already-completed setup seeded with the broken value. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 97 AND Patch = 0");
        }
    }
}
