using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_104_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 104, 0, GETUTCDATE(), 'Added Admin -> Settings -> Roles and -> Users, the M14.5 admin UI. Roles: create/rename/delete roles and edit each role''s permission matrix against a fixed, known catalog of Resource.Action pairs. Users: create an account, change roles, enable/disable (reuses Identity''s own lockout), and reset a password -- the only way to provision an account now that self-registration is disabled. Two guard rails block removing or disabling the last Administrator, since there is no recovery path for either short of editing the database directly. Administrator itself is protected from rename/delete/matrix-edit -- PermissionService bypasses the Permission table for it by name. Web-only, no node change.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 104 AND Patch = 0");
        }
    }
}
