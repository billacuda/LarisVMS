using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_192_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 192, 0, GETUTCDATE(), 'Added: media proxy tier (failover plan phase 2). A media proxy is a standalone relay (LarisVMS.Proxy) between browsers and recorder nodes for live view and playback - a dumb TLS-terminating pass-through, for when a proxy can hold a real certificate but the nodes cannot, or to keep media traffic off this server. Build with build-proxy.ps1 (or deploy.ps1 -BuildProxy), install with install-proxy.ps1 (reuses the node registration key). It appears on the new Admin - Media proxies page after its first check-in; set its host and enable it, then assign a primary (and optional backup) proxy to a node on Admin - Nodes. Browsers route through the first healthy proxy, falling back to direct-to-node then this server - a proxy this server cannot health-check is never used, so assigning one is safe. Also: nodes now name the camera (not just its id) in live/playback logs. Nodes auto-update; proxies do not.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 192 AND Patch = 0");
        }
    }
}
