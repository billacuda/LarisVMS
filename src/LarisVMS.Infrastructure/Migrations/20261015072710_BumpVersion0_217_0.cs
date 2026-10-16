using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BumpVersion0_217_0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                INSERT INTO AppVersions (Major, Minor, Patch, ReleasedAt, Notes)
                VALUES (0, 217, 0, GETUTCDATE(), 'Added: customizable dashboard, two-way audio, activity heatmaps, privacy masks and export redaction, camera map, object colors, speed traps with a speed log, playback markers, snapshots filter and heatmap, detection alerts, footage encryption, and tamper evidence. Changed: one Nodes page with builds and node settings, in the sidebar. Fixed: leftover footage on nodes, H.265 masked cameras reconnecting, ERR_CACHE_MISS on Back, G.711 audio playback.')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM AppVersions WHERE Major = 0 AND Minor = 217 AND Patch = 0");
        }
    }
}
