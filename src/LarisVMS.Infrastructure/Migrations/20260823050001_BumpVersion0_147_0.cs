using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_147_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 147, 0, GETUTCDATE(), 'Roles & permissions overhaul, pass 5 of 5 (final): auto-expiring role assignments. A background sweep removes an expired role from a user every minute, disabling the account if that was their last role, with a defensive guard against ever stripping the last enabled Super Admin. The Users page now auto-creates an expiry when an auto-expiring role is assigned, shows each user''s active expiries with an Extend action, and cleans up the row when the role is removed. Login re-validation now catches an already-expired-but-not-yet-swept role immediately. Completes the roles & permissions overhaul plan. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 147 AND Patch = 0");
        }
    }
}
