using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_6_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 6, 0, GETUTCDATE(), 'M5 pass 1 - Live view: recording now tees its one RTSP session into a live fMP4 fanout (no second camera session needed); nodes host a token-gated, LAN-only /live WebSocket reached only via LarisVMS.Web''s proxy (browser never talks to a node directly, resolving the open node-TLS question); browser-side MSE player on Pages/Live. Codec-fallback, main/sub auto-switch, snapshots, and instant replay are not in this pass. See CHANGELOG for verification notes and known limitations.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 6 AND Patch = 0");
        }
    }
}
