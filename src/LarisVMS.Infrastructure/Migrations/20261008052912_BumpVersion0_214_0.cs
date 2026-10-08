using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_214_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 214, 0, GETUTCDATE(), 'Fixed: settings pages that silently kept their old values on save; node/proxy auto-update leaving the service stopped; the web service crashing on a bad certificate or timing out on a slow database; the setup wizard staying reachable after setup. Changed: installer upgrades skip the settings pages, certificates are checked in the installer, the setup wizard reuses an existing LarisVMS database, and only the newest node/proxy build awaits approval.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 214 AND Patch = 0");
        }
    }
}
