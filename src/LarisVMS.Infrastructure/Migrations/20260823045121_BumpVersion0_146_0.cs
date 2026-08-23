using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_146_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 146, 0, GETUTCDATE(), 'Roles & permissions overhaul, pass 4 of 5: PTZ priority arbitration. A requester whose held roles carry a PTZ priority now locks out lower-or-equal-priority PTZ commands on a camera until their own lockout window elapses after their last command; a denied command returns 409 with a retry-after, surfaced on the PTZ pad. A role with no configured PTZ priority skips arbitration entirely, keeping unrestricted first-come behavior unchanged. In-memory only, not persisted. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 146 AND Patch = 0");
        }
    }
}
