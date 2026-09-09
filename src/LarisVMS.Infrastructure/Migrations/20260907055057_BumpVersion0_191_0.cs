using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_191_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 191, 0, GETUTCDATE(), 'Added: direct-to-node streaming (failover plan phase 1). Live view and playback can connect a browser straight to the recorder node over HTTPS instead of relaying every byte through this server. Off by default; turn it on at Admin - Settings - Live View, or per node on Admin - Nodes. Each node needs an HTTPS client endpoint: install-node.ps1 gained -ClientPort / -ClientPfxPath / -ClientPfxPassword / -ClientAllowInsecure, or set a cert path per node. For setup only a node can auto-generate a self-signed certificate - viewers click through a browser warning and the stream is flagged insecure. A camera whose node has not reported a healthy client endpoint stays on the proxy, so enabling the toggle is safe. Admin - Nodes shows a relay counter that trends to zero as cameras move to direct. Nodes auto-update; re-run install-node.ps1 on a node to enable its client endpoint.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 191 AND Patch = 0");
        }
    }
}
