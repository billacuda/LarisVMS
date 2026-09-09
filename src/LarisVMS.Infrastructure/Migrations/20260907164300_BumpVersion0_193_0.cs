using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_193_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 193, 0, GETUTCDATE(), 'Added: media proxies now auto-update exactly like recorder nodes. deploy.ps1 -BuildProxy registers each new proxy build on Admin - Node Builds (platform proxy-win-x64); once an admin approves it, every proxy whose reported version is older downloads it, verifies its SHA-256, and swaps its own binary on its next check-in - no more re-running install-proxy.ps1 on each relay machine. The proxy package now bundles LarisVMS.NodeUpdater.exe for this. Controlled by the same Auto-update setting on Admin - Settings - Nodes; an update briefly drops in-flight live/playback connections while the service restarts. Also: a copy button next to the install-proxy.ps1 command on Admin - Media proxies.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 193 AND Patch = 0");
        }
    }
}
