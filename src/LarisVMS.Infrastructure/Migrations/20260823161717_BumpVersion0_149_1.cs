using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_149_1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 149, 1, GETUTCDATE(), 'Added: M20 pass 2, IP allow lists. Admin > Settings > Security (revived) holds two independent allow lists - management/API and live view/playback - of individual IPs or CIDR blocks, reusing the existing management-vs-media request split so GET /api/v1/status falls under the management/API list automatically. Blank means unrestricted. Enforced by IpAllowListMiddleware, rejecting with 404 to avoid revealing anything to a request outside the boundary. No new table - two Setting rows. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 149 AND Patch = 1");
        }
    }
}
