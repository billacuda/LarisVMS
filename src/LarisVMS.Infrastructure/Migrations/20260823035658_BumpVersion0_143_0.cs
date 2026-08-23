using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_143_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 143, 0, GETUTCDATE(), 'Roles & permissions overhaul, pass 1 of 5: new RoleProfile/RoleAssignmentExpiry tables backing 8 named roles (Super Admin, System Admin, Security Manager, Operator, Investigator/Auditor, Installer/Technician, Guest/Viewer, API/Integration), each with a scope tier, auto-expiry default, and PTZ priority/lockout. Administrator and Viewer renamed in place to Super Admin and Guest/Viewer (same role Id, no remapping needed); the Super Admin matrix-bypass now resolves via a role tag instead of the literal name. Admin > Settings'' Roles, Users, and Security tabs consolidated into one Permissions page with Matrix/Roles/Users sub-tabs; the live/playback port setting moved to Live View. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 143 AND Patch = 0");
        }
    }
}
