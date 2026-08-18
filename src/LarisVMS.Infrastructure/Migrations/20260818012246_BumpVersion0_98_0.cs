using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_98_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 98, 0, GETUTCDATE(), 'Added: session lifetime is now configurable per role (Admin > Settings > Security), replacing the previous fixed 60-minute cookie timeout. Defaults to 24 hours; 0 means that role never expires. A user holding more than one role is bound by whichever role''s own limit is shortest, so the setting cannot be bypassed by holding an additional unlimited role. Enforced in the cookie OnValidatePrincipal handler, chained after rather than replacing ASP.NET Core Identity''s own security-stamp revalidation, so a password change still signs a user out everywhere. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 98 AND Patch = 0");
        }
    }
}
