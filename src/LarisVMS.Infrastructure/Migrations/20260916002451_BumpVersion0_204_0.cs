using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_204_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 204, 0, GETUTCDATE(), 'LarisVMS.Web no longer requires IIS: it now self-hosts Kestrel directly as its own Windows Service, the same way the recorder node already does. Install/upgrade with the new install-web.ps1 script instead of deploy.ps1 + an IIS site. Supports both a domain/service account (SQL Integrated Security) and SQL Authentication. HTTPS certificate comes from Kestrel:Certificates:Default:Path/:Password in appsettings.Production.json, hot-reloaded every 60 seconds from a file share the same way the node''s client endpoint already does, falling back to a self-signed certificate when none is configured yet so a fresh install is always reachable. The live/playback custom-port setting now opens its own Kestrel listener directly (restart required to apply) instead of relying on a manually-added IIS site binding.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 204 AND Patch = 0");
        }
    }
}
